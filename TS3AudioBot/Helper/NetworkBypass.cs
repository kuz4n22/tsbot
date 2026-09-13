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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;

namespace TS3AudioBot.Helper;

/// <summary>
/// TSBot addition: optional DPI-bypass for the bot's own downloads only.
/// Runs ByeDPI (ciadpi, a local SOCKS5 proxy that fragments/desyncs TLS handshakes),
/// exposes a tiny local HTTP CONNECT bridge in front of it (ffmpeg cannot speak SOCKS),
/// and points yt-dlp/ffmpeg/HttpClient at it through the process environment.
/// Nothing else on the machine is affected.
/// </summary>
public static class NetworkBypass
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
	private static Process? proxyProcess;
	private static SocksBridge? bridge;
	private static Timer? watchdog;
	private static ConfBypass? conf;
	private static readonly object sync = new();

	public static bool Active => bridge != null;

	public static void Start(ConfBypass config)
	{
		conf = config;
		if (!config.Enabled.Value)
			return;

		var socks = ParseEndpoint(config.Socks.Value, 1080);
		if (socks is null)
		{
			Log.Error("bypass: invalid socks endpoint '{0}'", config.Socks.Value);
			return;
		}

		StartProxyProcess();

		try
		{
			bridge = new SocksBridge(socks.Value.host, socks.Value.port, config.BridgePort.Value);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "bypass: could not start the local HTTP bridge on port {0}", config.BridgePort.Value);
			return;
		}

		var url = $"http://127.0.0.1:{bridge.Port}";
		foreach (var name in new[] { "http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY" })
			System.Environment.SetEnvironmentVariable(name, url);
		// keep local traffic (web api, teamspeak) out of the proxy
		System.Environment.SetEnvironmentVariable("no_proxy", "localhost,127.0.0.1,::1");
		System.Environment.SetEnvironmentVariable("NO_PROXY", "localhost,127.0.0.1,::1");
		Log.Info("bypass: yt-dlp/ffmpeg will use {0} -> socks5 {1}:{2}", url, socks.Value.host, socks.Value.port);

		watchdog = new Timer(_ => EnsureProxyAlive(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
	}

	private static void StartProxyProcess()
	{
		if (conf is null || string.IsNullOrWhiteSpace(conf.Path.Value))
			return;
		var path = Path.GetFullPath(conf.Path.Value);
		if (!File.Exists(path))
		{
			Log.Error("bypass: ciadpi not found at {0}", path);
			return;
		}
		lock (sync)
		{
			try
			{
				var p = new Process
				{
					StartInfo = new ProcessStartInfo
					{
						FileName = path,
						Arguments = conf.Args.Value,
						UseShellExecute = false,
						CreateNoWindow = true,
						RedirectStandardOutput = true,
						RedirectStandardError = true,
						WorkingDirectory = Path.GetDirectoryName(path) ?? ".",
					},
					EnableRaisingEvents = true,
				};
				p.OutputDataReceived += (s, e) => { if (e.Data != null) Log.Debug("ciadpi: {0}", e.Data); };
				p.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.Debug("ciadpi: {0}", e.Data); };
				p.Start();
				p.BeginOutputReadLine();
				p.BeginErrorReadLine();
				proxyProcess = p;
				Log.Info("bypass: started ciadpi (pid {0}) {1}", p.Id, conf.Args.Value);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "bypass: failed to start ciadpi");
			}
		}
	}

	private static void EnsureProxyAlive()
	{
		var p = proxyProcess;
		if (p is null || string.IsNullOrWhiteSpace(conf?.Path.Value))
			return;
		bool exited;
		try { exited = p.HasExited; }
		catch { exited = true; }
		if (exited)
		{
			Log.Warn("bypass: ciadpi exited (code {0}), restarting", SafeExitCode(p));
			StartProxyProcess();
		}
	}

	private static string SafeExitCode(Process p)
	{
		try { return p.ExitCode.ToString(); } catch { return "?"; }
	}

	public static void Stop()
	{
		watchdog?.Dispose();
		watchdog = null;
		bridge?.Dispose();
		bridge = null;
		var p = proxyProcess;
		proxyProcess = null;
		if (p != null)
		{
			try { if (!p.HasExited) p.Kill(); } catch (Exception ex) { Log.Debug(ex, "bypass: kill failed"); }
			p.Dispose();
		}
	}

	private static (string host, int port)? ParseEndpoint(string value, int defaultPort)
	{
		if (string.IsNullOrWhiteSpace(value))
			return null;
		var idx = value.LastIndexOf(':');
		if (idx < 0)
			return (value, defaultPort);
		return int.TryParse(value[(idx + 1)..], out var port) ? (value[..idx], port) : null;
	}
}

/// <summary>Minimal HTTP CONNECT proxy that forwards every tunnel to a SOCKS5 proxy (domain names are passed through, so the SOCKS side does the DNS).</summary>
public sealed class SocksBridge : IDisposable
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
	private readonly TcpListener listener;
	private readonly string socksHost;
	private readonly int socksPort;
	private readonly CancellationTokenSource cts = new();

	public int Port { get; }

	public SocksBridge(string socksHost, int socksPort, int listenPort)
	{
		this.socksHost = socksHost;
		this.socksPort = socksPort;
		listener = new TcpListener(IPAddress.Loopback, listenPort);
		listener.Start();
		Port = ((IPEndPoint)listener.LocalEndpoint).Port;
		_ = AcceptLoop();
	}

	private async Task AcceptLoop()
	{
		while (!cts.IsCancellationRequested)
		{
			TcpClient client;
			try { client = await listener.AcceptTcpClientAsync(cts.Token); }
			catch (OperationCanceledException) { break; }
			catch (ObjectDisposedException) { break; }
			catch (Exception ex) { Log.Debug(ex, "bridge: accept failed"); continue; }
			_ = Handle(client);
		}
	}

	private async Task Handle(TcpClient client)
	{
		using (client)
		{
			try
			{
				client.NoDelay = true;
				var stream = client.GetStream();

				var buf = new byte[16 * 1024];
				int len = 0, headEnd;
				while (true)
				{
					int n = await stream.ReadAsync(buf.AsMemory(len, buf.Length - len), cts.Token);
					if (n <= 0) return;
					len += n;
					headEnd = FindHeaderEnd(buf, len);
					if (headEnd >= 0) break;
					if (len == buf.Length) return;
				}

				var head = Encoding.ASCII.GetString(buf, 0, headEnd);
				var line = head.Split("\r\n", 2)[0];
				var parts = line.Split(' ');
				if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
				{
					await Write(stream, "HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\n\r\n");
					return;
				}
				var target = parts[1];
				var colon = target.LastIndexOf(':');
				var host = colon > 0 ? target[..colon] : target;
				var port = colon > 0 && int.TryParse(target[(colon + 1)..], out var pp) ? pp : 443;
				if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];

				using var socks = new TcpClient();
				await socks.ConnectAsync(socksHost, socksPort, cts.Token);
				socks.NoDelay = true;
				var ss = socks.GetStream();

				await ss.WriteAsync(new byte[] { 5, 1, 0 }, cts.Token);
				var rep = new byte[2];
				await ReadExact(ss, rep, 2);
				if (rep[0] != 5 || rep[1] != 0)
				{
					await Write(stream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n");
					return;
				}

				byte[] req;
				if (IPAddress.TryParse(host, out var ip))
				{
					var ab = ip.GetAddressBytes();
					req = new byte[4 + ab.Length + 2];
					req[0] = 5; req[1] = 1; req[2] = 0; req[3] = (byte)(ab.Length == 4 ? 1 : 4);
					ab.CopyTo(req, 4);
					req[^2] = (byte)(port >> 8); req[^1] = (byte)port;
				}
				else
				{
					var hb = Encoding.ASCII.GetBytes(host);
					req = new byte[5 + hb.Length + 2];
					req[0] = 5; req[1] = 1; req[2] = 0; req[3] = 3; req[4] = (byte)hb.Length;
					hb.CopyTo(req, 5);
					req[^2] = (byte)(port >> 8); req[^1] = (byte)port;
				}
				await ss.WriteAsync(req, cts.Token);

				var resp = new byte[4];
				await ReadExact(ss, resp, 4);
				if (resp[1] != 0)
				{
					Log.Debug("bridge: socks connect to {0}:{1} failed with code {2}", host, port, resp[1]);
					await Write(stream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n");
					return;
				}
				int skip = resp[3] switch { 1 => 4, 4 => 16, 3 => -1, _ => 0 };
				if (skip == -1)
				{
					var l = new byte[1];
					await ReadExact(ss, l, 1);
					skip = l[0];
				}
				var junk = new byte[skip + 2];
				await ReadExact(ss, junk, junk.Length);

				await Write(stream, "HTTP/1.1 200 Connection established\r\n\r\n");
				if (len > headEnd)
					await ss.WriteAsync(buf.AsMemory(headEnd, len - headEnd), cts.Token);

				var up = stream.CopyToAsync(ss, 64 * 1024, cts.Token);
				var down = ss.CopyToAsync(stream, 64 * 1024, cts.Token);
				await Task.WhenAny(up, down);
				// one side is done: close both ends, then observe the other copy so its exception never goes unobserved
				try { client.Close(); } catch { }
				try { socks.Close(); } catch { }
				try { await Task.WhenAll(up, down); } catch { }
			}
			catch (OperationCanceledException) { }
			catch (IOException) { }
			catch (SocketException) { }
			catch (Exception ex)
			{
				Log.Debug(ex, "bridge: tunnel error");
			}
		}
	}

	private static int FindHeaderEnd(byte[] buf, int len)
	{
		for (int i = 3; i < len; i++)
			if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n')
				return i + 1;
		return -1;
	}

	private async Task ReadExact(NetworkStream s, byte[] buf, int count)
	{
		int off = 0;
		while (off < count)
		{
			int n = await s.ReadAsync(buf.AsMemory(off, count - off), cts.Token);
			if (n <= 0) throw new IOException("socks stream closed");
			off += n;
		}
	}

	private async Task Write(NetworkStream s, string text)
		=> await s.WriteAsync(Encoding.ASCII.GetBytes(text), cts.Token);

	public void Dispose()
	{
		cts.Cancel();
		try { listener.Stop(); } catch { }
		cts.Dispose();
	}
}
