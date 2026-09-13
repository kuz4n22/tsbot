// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TS3AudioBot.ResourceFactories;

/// <summary>
/// TSBot addition: keeps yt-dlp current on its own.
///
/// YouTube changes how links are signed every few weeks and an outdated yt-dlp then fails to
/// extract anything. That failure reads differently from "this video is gone", so it can be
/// recognised: when it happens the bot updates yt-dlp in place and retries the same request,
/// instead of leaving someone to figure out that a script needs running. On top of that it
/// checks for a new version once a day, which usually fixes the problem before anyone hits it.
/// </summary>
public static class YtDlpUpdater
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

	/// <summary>What a broken extractor sounds like, as opposed to an unavailable video.</summary>
	private static readonly string[] OutdatedSigns =
	[
		"nsig extraction failed",
		"unable to extract nsig",
		"signature extraction failed",
		"unable to extract",
		"failed to extract any player response",
		"please report this issue",
		"sign in to confirm you're not a bot",
		"requested format is not available",
	];

	private static readonly TimeSpan MinBetweenUpdates = TimeSpan.FromHours(1);
	private static readonly TimeSpan RoutineInterval = TimeSpan.FromHours(24);
	private static readonly SemaphoreSlim gate = new(1, 1);

	private static DateTime lastUpdate = DateTime.MinValue;
	private static Timer? routine;
	private static string? lastKnownPath;

	/// <summary>True when the error is yt-dlp being out of date rather than a problem with that one video.</summary>
	public static bool LooksOutdated(string? error)
	{
		if (string.IsNullOrEmpty(error))
			return false;
		foreach (var sign in OutdatedSigns)
			if (error.Contains(sign, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	/// <summary>Remembers where yt-dlp lives and arms the daily check on the first call.</summary>
	public static void Seen(string path)
	{
		lastKnownPath = path;
		if (routine != null)
			return;
		routine = new Timer(_ => _ = TryUpdate(null, routine: true), null, RoutineInterval, RoutineInterval);
		Log.Debug("yt-dlp: daily update check armed");
	}

	/// <summary>
	/// Runs "yt-dlp -U". Returns true only when a new version was actually installed, so the caller
	/// knows whether retrying makes sense. At most one update per hour, whatever happens.
	/// </summary>
	public static async Task<bool> TryUpdate(string? path, bool routine = false)
	{
		path ??= lastKnownPath;
		if (path is null)
			return false;

		if (!await gate.WaitAsync(TimeSpan.Zero))
			return false; // another update is already running
		try
		{
			if (DateTime.UtcNow - lastUpdate < MinBetweenUpdates)
				return false;
			lastUpdate = DateTime.UtcNow;

			Log.Info("yt-dlp: checking for a new version ({0})", routine ? "daily check" : "YouTube changed something");
			var output = await RunUpdate(path);
			var updated = output.Contains("Updated yt-dlp", StringComparison.OrdinalIgnoreCase)
				|| output.Contains("Updating to", StringComparison.OrdinalIgnoreCase);
			if (updated)
				Log.Info("yt-dlp: updated - {0}", Summarize(output));
			else
				Log.Info("yt-dlp: already the newest version");
			return updated;
		}
		catch (Exception ex)
		{
			Log.Warn("yt-dlp: update failed ({0})", ex.Message);
			return false;
		}
		finally
		{
			gate.Release();
		}
	}

	private static async Task<string> RunUpdate(string path)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = path,
				Arguments = "-U",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			},
		};
		var output = new StringBuilder();
		process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
		process.ErrorDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		await process.WaitForExitAsync(timeout.Token);
		return output.ToString();
	}

	private static string Summarize(string output)
	{
		foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			if (line.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase))
				return line.Trim();
		return output.Trim();
	}
}
