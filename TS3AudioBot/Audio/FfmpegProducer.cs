// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TSLib.Audio;
using TSLib.Helper;
using TSLib.Scheduler;

namespace TS3AudioBot.Audio;

public sealed class FfmpegProducer : IPlayerSource, IDisposable
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
	private readonly Id id;
	private static readonly Regex FindDurationMatch = new(@"^\s*Duration: (\d+):(\d\d):(\d\d).(\d\d)", Util.DefaultRegexConfig);
	private static readonly Regex IcyMetadataMacher = new("((\\w+)='(.*?)';\\s*)+", Util.DefaultRegexConfig);
	// TSBot: never let a stalled stream block the bot - ffmpeg gives up after 10 s without data and exits,
// which surfaces as a normal "song ended" (and the reconnect logic below retries a few times first).
	private const string PreLinkConf = "-hide_banner -nostats -threads 1 -rw_timeout 10000000 -reconnect 1 -reconnect_streamed 1 -reconnect_on_network_error 1 -reconnect_delay_max 4 -i \"";
	private const string PostLinkConf = "\" -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
	private const string LinkConfIcy = "-hide_banner -nostats -threads 1 -i pipe:0 -ac 2 -ar 48000 -f s16le -acodec pcm_s16le pipe:1";
	private static readonly TimeSpan retryOnDropBeforeEnd = TimeSpan.FromSeconds(10);
	/// <summary>TSBot: a stream that has not produced a single sample after this long is not going to.</summary>
	private static readonly TimeSpan StartSilenceTimeout = TimeSpan.FromSeconds(8);

	private readonly ConfToolsFfmpeg config;

	public event EventHandler? OnSongEnd;
	public event EventHandler<SongInfoChanged>? OnSongUpdated;

	private readonly DedicatedTaskScheduler scheduler;
	private FfmpegInstance? ffmpegInstance;
	public SampleInfo SampleInfo { get; } = SampleInfo.OpusMusic;

	public FfmpegProducer(ConfToolsFfmpeg config, DedicatedTaskScheduler scheduler, Id id)
	{
		this.config = config;
		this.scheduler = scheduler;
		this.id = id;
	}

	public Task AudioStart(string url, TimeSpan? startOff = null)
	{
		StartFfmpegProcess(url, startOff ?? TimeSpan.Zero);
		return Task.CompletedTask;
	}

	public async Task AudioStartIcy(string url)
	{
		if (!(await StartFfmpegProcessIcy(url)).Get(out _, out var error))
		{
			Log.Warn("Failed to start icy stream: {0}", error);
		}
	}

	public void AudioStop()
	{
		StopFfmpegProcess();
	}

	public TimeSpan? Length => GetCurrentSongLength();

	public TimeSpan? Position => ffmpegInstance?.AudioTimer.SongPosition;

	public Task Seek(TimeSpan position) { SetPosition(position); return Task.CompletedTask; }

	public int Read(Span<byte> data, out Meta? meta)
	{
		meta = default;
		int read;

		var instance = ffmpegInstance;

		if (instance is null)
			return 0;

		try
		{
			read = instance.FfmpegProcess.StandardOutput.BaseStream.Read(data);
		}
		catch (Exception ex)
		{
			read = 0;
			Log.Debug(ex, "Can't read ffmpeg");
		}

		if (read == 0)
		{
			AssertNotMainScheduler();

			var (ret, triggerEndSafe) = instance.IsIcyStream
				? OnReadEmptyIcy(instance)
				: OnReadEmpty(instance);
			if (ret)
				return 0;

			if (instance.FfmpegProcess.HasExitedSafe())
			{
				Log.Trace("Ffmpeg has exited");
				AudioStop();
				triggerEndSafe = true;
			}

			if (triggerEndSafe)
			{
				OnSongEnd?.Invoke(this, EventArgs.Empty);
				return 0;
			}
		}

		if (read > 0 && !instance.ProducedData)
		{
			instance.ProducedData = true; // TSBot: the stream is alive, no need to watch it any more
			instance.SilenceWatchdog?.Dispose();
			instance.SilenceWatchdog = null;
		}

		instance.HasTriedToReconnect = false;
		instance.ReconnectAttempts = 0;
		instance.AudioTimer.PushBytes(read);
		return read;
	}

	/// <summary>TSBot: drops a stream that opened but stayed silent, so the audio thread stops waiting on it.</summary>
	private void KillIfSilent(FfmpegInstance instance)
	{
		if (instance.ProducedData || instance.Closed || instance.FfmpegProcess.HasExitedSafe())
			return;
		Log.Warn("No audio {0:0} s after the stream opened, dropping it", StartSilenceTimeout.TotalSeconds);
		try { instance.FfmpegProcess.Kill(); }
		catch (Exception ex) { Log.Debug(ex, "Could not stop the silent ffmpeg"); }
	}

	private (bool ret, bool trigger) OnReadEmpty(FfmpegInstance instance)
	{
		// TSBot: not a single sample came out of a seeked stream - YouTube serves some long files
		// only from the start. Play the song from the beginning instead of losing it.
		if (instance.FfmpegProcess.HasExitedSafe() && !instance.ProducedData
			&& instance.StartOffset > TimeSpan.Zero && instance.ReconnectAttempts < 3)
		{
			var seekAttempt = instance.ReconnectAttempts + 1;
			Log.Warn("No audio after seeking to {0:g}, starting the song from the beginning", instance.StartOffset);
			if (StartFfmpegProcess(instance.ReconnectUrl, TimeSpan.Zero).Get(out var restarted, out var startError))
			{
				restarted.ReconnectAttempts = seekAttempt;
				return (true, false);
			}
			Log.Debug("Restart without seek failed: {0}", startError);
			return (false, true);
		}

		if (instance.FfmpegProcess.HasExitedSafe() && instance.ReconnectAttempts < 3)
		{
			var expectedStopLength = GetCurrentSongLength();
			Log.Trace("Expected song length {0}", expectedStopLength);
			if (expectedStopLength != TimeSpan.Zero)
			{
				var actualStopPosition = instance.AudioTimer.SongPosition;
				Log.Trace("Actual song position {0}", actualStopPosition);
				if (actualStopPosition + retryOnDropBeforeEnd < expectedStopLength)
				{
					var attempt = instance.ReconnectAttempts + 1;
					Log.Debug("Connection to song lost, retrying at {0} (attempt {1})", actualStopPosition, attempt);
					instance.ReconnectAttempts = attempt;
					instance.HasTriedToReconnect = true;
					if (attempt > 1)
						Thread.Sleep(1500); // transient CDN errors (400/EOF) usually clear within a second
					if (SetPosition(actualStopPosition).Get(out var newInstance, out var error))
					{
						newInstance.ReconnectAttempts = attempt;
						newInstance.HasTriedToReconnect = true;
						return (true, false);
					}
					else
					{
						Log.Debug("Retry failed {0}", error);
						return (false, true);
					}
				}
			}
		}
		return (false, false);
	}

	private (bool ret, bool trigger) OnReadEmptyIcy(FfmpegInstance instance)
	{
		AssertNotMainScheduler();

		if (instance.FfmpegProcess.HasExitedSafe() && !instance.HasTriedToReconnect)
		{
			Log.Debug("Connection to stream lost, retrying...");
			instance.HasTriedToReconnect = true;
			var newInstance = StartFfmpegProcessIcy(instance.ReconnectUrl).Result;
			if (newInstance.Ok)
			{
				newInstance.Value.HasTriedToReconnect = true;
				return (true, false);
			}
			else
			{
				Log.Debug("Retry failed {0}", newInstance.Error);
				return (false, true);
			}
		}
		return (false, false);
	}

	private R<FfmpegInstance, string> SetPosition(TimeSpan value)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);

		var instance = ffmpegInstance;
		if (instance is null)
			return "No instance running";
		if (instance.IsIcyStream)
			return "Cannot seek icy stream";
		var lastLink = instance.ReconnectUrl;
		if (lastLink is null)
			return "No current url active";
		return StartFfmpegProcess(lastLink, value);
	}

	private R<FfmpegInstance, string> StartFfmpegProcess(string url, TimeSpan? offsetOpt)
	{
		StopFfmpegProcess();
		Log.Trace("Start request {0}", url);

		string arguments;
		var offset = offsetOpt ?? TimeSpan.Zero;
		if (offset > TimeSpan.Zero)
		{
			var seek = string.Format(CultureInfo.InvariantCulture, @"-ss {0:hh\:mm\:ss\.fff}", offset);
			arguments = string.Concat(seek, " ", PreLinkConf, url, PostLinkConf, " ", seek);
		}
		else
		{
			arguments = string.Concat(PreLinkConf, url, PostLinkConf);
		}

		var newInstance = new FfmpegInstance(
			url,
			new PreciseAudioTimer(SampleInfo)
			{
				SongPositionOffset = offset,
			})
		{
			StartOffset = offset, // TSBot: remembered so a seek YouTube refuses can be retried from the start
		};

		return StartFfmpegProcessInternal(newInstance, arguments);
	}

	private async Task<R<FfmpegInstance, string>> StartFfmpegProcessIcy(string url)
	{
		StopFfmpegProcess();
		Log.Trace("Start icy-stream request {0}", url);

		try
		{
			var response = await WebWrapper
				.Request(url)
				.WithHeader("Icy-MetaData", "1")
				.UnsafeResponse();

			if (!int.TryParse(response.Headers.GetSingle("icy-metaint"), out var metaint))
			{
				response.Dispose();
				return "Invalid icy stream tags";
			}

			var stream = await response.Content.ReadAsStreamAsync();
			var newInstance = new FfmpegInstance(
				url,
				new PreciseAudioTimer(SampleInfo),
				stream,
				metaint)
			{
				OnMetaUpdated = e => OnSongUpdated?.Invoke(this, e)
			};

			new Thread(() => newInstance.ReadStreamLoop(id))
			{
				Name = $"IcyStreamReader[{id}]",
			}.Start();

			return StartFfmpegProcessInternal(newInstance, LinkConfIcy);
		}
		catch (Exception ex)
		{
			var error = $"Unable to create icy-stream ({ex.Message})";
			Log.Warn(ex, error);
			return error;
		}
	}

	private R<FfmpegInstance, string> StartFfmpegProcessInternal(FfmpegInstance instance, string arguments)
	{
		try
		{
			instance.FfmpegProcess.StartInfo = new ProcessStartInfo
			{
				FileName = config.Path.Value,
				Arguments = arguments,
				RedirectStandardOutput = true,
				RedirectStandardInput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			instance.FfmpegProcess.EnableRaisingEvents = true;

			Log.Debug("Starting ffmpeg with {0}", arguments);
			instance.FfmpegProcess.ErrorDataReceived += instance.FfmpegProcess_ErrorDataReceived;
			instance.FfmpegProcess.Start();
			instance.FfmpegProcess.BeginErrorReadLine();

			instance.AudioTimer.Start();
			// TSBot: a stream that opens but never delivers audio (YouTube refusing a seek, a dead CDN
			// edge) would keep the audio thread waiting on the pipe forever - drop it instead.
			instance.SilenceWatchdog = new Timer(_ => KillIfSilent(instance), null, StartSilenceTimeout, Timeout.InfiniteTimeSpan);

			var oldInstance = Interlocked.Exchange(ref ffmpegInstance, instance);
			oldInstance?.Close();

			return instance;
		}
		catch (Exception ex)
		{
			var error = ex is Win32Exception
				? $"Ffmpeg could not be found ({ex.Message})"
				: $"Unable to create stream ({ex.Message})";
			Log.Error(ex, error);
			instance.Close();
			StopFfmpegProcess();
			return error;
		}
	}

	private void StopFfmpegProcess()
	{
		var oldInstance = Interlocked.Exchange(ref ffmpegInstance, null);
		if (oldInstance != null)
		{
			oldInstance.OnMetaUpdated = null;
			oldInstance.Close();
		}
	}

	private TimeSpan? lastKnownLength;
	private TimeSpan? GetCurrentSongLength()
	{
		var len = ffmpegInstance?.ParsedSongLength;
		if (len is { } l && l > TimeSpan.Zero)
			lastKnownLength = l;
		return len ?? lastKnownLength;
	}

	private void AssertNotMainScheduler()
	{
		if (TaskScheduler.Current == scheduler)
			throw new Exception("Cannot read on own scheduler. Throwing to prevent deadlock");
	}

	public void Dispose()
	{
		StopFfmpegProcess();
	}

	private class FfmpegInstance
	{
		public Process FfmpegProcess { get; }
		public bool HasTriedToReconnect { get; set; }
		public int ReconnectAttempts { get; set; }
		public string ReconnectUrl { get; }
		public bool IsIcyStream => IcyStream != null;

		public PreciseAudioTimer AudioTimer { get; }
		public TimeSpan? ParsedSongLength { get; set; } = null;

		/// <summary>TSBot: where playback was asked to start; zero when the song plays from the beginning.</summary>
		public TimeSpan StartOffset { get; init; }
		/// <summary>TSBot: set once the first audio arrives - a stream that never sets it is dead.</summary>
		public bool ProducedData { get; set; }
		/// <summary>TSBot: drops the process when it stays silent after starting.</summary>
		public Timer? SilenceWatchdog { get; set; }

		public Stream? IcyStream { get; }
		public int IcyMetaInt { get; }
		public bool Closed { get; set; }

		public Action<SongInfoChanged>? OnMetaUpdated;

		public FfmpegInstance(string url, PreciseAudioTimer timer) : this(url, timer, null!, 0) { }
		public FfmpegInstance(string url, PreciseAudioTimer timer, Stream icyStream, int icyMetaInt)
		{
			FfmpegProcess = new Process();
			ReconnectUrl = url;
			AudioTimer = timer;
			IcyStream = icyStream;
			IcyMetaInt = icyMetaInt;

			HasTriedToReconnect = false;
		}

		public void Close()
		{
			Closed = true;
			SilenceWatchdog?.Dispose();
			SilenceWatchdog = null;

			try
			{
				if (!FfmpegProcess.HasExitedSafe())
					FfmpegProcess.Kill();
			}
			catch (Exception ex) { Log.Debug(ex, "Failed killing ffmpeg"); }
			try { FfmpegProcess.Dispose(); } catch { }

			IcyStream?.Dispose();
		}

		public void FfmpegProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
		{
			if (e.Data is null)
				return;

			if (sender != FfmpegProcess)
				throw new InvalidOperationException("Wrong process associated to event");

			if (ParsedSongLength is null)
			{
				var match = FindDurationMatch.Match(e.Data);
				if (!match.Success)
					return;

				int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
				int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
				int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
				int millisec = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) * 10;
				ParsedSongLength = new TimeSpan(0, hours, minutes, seconds, millisec);
			}

			//if (!HasIcyTag && e.Data.AsSpan().TrimStart().StartsWith("icy-".AsSpan()))
			//{
			//	HasIcyTag = true;
			//}
		}

		public void ReadStreamLoop(Id id)
		{
			if (IcyStream is null)
				throw new InvalidOperationException("Instance is not an icy stream");

			Tools.SetLogId(id.ToString());
			const int IcyMaxMeta = 255 * 16;
			const int ReadBufferSize = 4096;

			int errorCount = 0;
			var buffer = new byte[Math.Max(ReadBufferSize, IcyMaxMeta)];
			int readCount = 0;

			while (!Closed)
			{
				try
				{
					while (readCount < IcyMetaInt)
					{
						int read = IcyStream.Read(buffer, 0, Math.Min(ReadBufferSize, IcyMetaInt - readCount));
						if (read == 0)
						{
							Close();
							return;
						}
						readCount += read;
						FfmpegProcess.StandardInput.BaseStream.Write(buffer, 0, read);
						errorCount = 0;
					}
					readCount = 0;

					var metaByte = IcyStream.ReadByte();
					if (metaByte < 0)
					{
						Close();
						return;
					}

					if (metaByte > 0)
					{
						metaByte *= 16;
						while (readCount < metaByte)
						{
							int read = IcyStream.Read(buffer, 0, metaByte - readCount);
							if (read == 0)
							{
								Close();
								return;
							}
							readCount += read;
						}
						readCount = 0;

						var metaString = Tools.Utf8Encoder.GetString(buffer, 0, metaByte).TrimEnd('\0');
						Log.Debug("Meta: {0}", metaString);
						OnMetaUpdated?.Invoke(ParseIcyMeta(metaString));
					}
				}
				catch (Exception ex)
				{
					errorCount++;
					if (errorCount >= 50)
					{
						Log.Error(ex, "Failed too many times trying to access ffmpeg. Closing stream.");
						Close();
						return;
					}

					if (ex is InvalidOperationException)
					{
						Log.Debug(ex, "Waiting for ffmpeg");
						Thread.Sleep(100);
					}
					else
					{
						Log.Debug(ex, "Stream read/write error");
					}
				}
			}
		}

		private static SongInfoChanged ParseIcyMeta(string metaString)
		{
			var songInfo = new SongInfoChanged();
			var match = IcyMetadataMacher.Match(metaString);
			if (match.Success)
			{
				for (int i = 0; i < match.Groups[1].Captures.Count; i++)
				{
					switch (match.Groups[2].Captures[i].Value.ToUpperInvariant())
					{
					case "STREAMTITLE":
						songInfo.Title = match.Groups[3].Captures[i].Value;
						break;
					}
				}
			}
			return songInfo;
		}
	}
}

// Icy: IcyLoop +=> FFmpeg -=> Buffer -=> TimePipe
// Nrm:             FFmpeg -=> Buffer -=> TimePipe
