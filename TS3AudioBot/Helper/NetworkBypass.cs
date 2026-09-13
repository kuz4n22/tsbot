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
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;

namespace TS3AudioBot.Helper;

/// <summary>
/// TSBot addition: keeps the bot's own YouTube traffic working where the provider blocks it,
/// without touching anything outside this process.
///
/// A direct connection is always preferred. Every minute a probe asks YouTube for a
/// 204; only while that fails does the bot start ByeDPI (ciadpi, a local SOCKS5 proxy that
/// desyncs TLS handshakes) and point its own yt-dlp/ffmpeg at it. The moment the direct
/// route works again - the user switched a VPN on, the provider stopped blocking - the
/// bypass is dropped and the bot goes straight out again.
///
/// The proxy is handed to the tools through this process' environment (which child
/// processes inherit), so the system, the browser and everything else stay untouched.
/// ffmpeg cannot speak SOCKS, hence the small HTTP CONNECT bridge in front of it.
/// </summary>
public static class NetworkBypass
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

	/// <summary>How the bot's downloads currently leave the machine.</summary>
	public enum Route
	{
		/// <summary>Not decided yet (the first probe has not finished).</summary>
		Unknown,
		/// <summary>Straight out - YouTube is reachable without help.</summary>
		Direct,
		/// <summary>Through the local ByeDPI proxy.</summary>
		Bypass,
	}

	private static readonly string[] ProxyVars = ["http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY"];
	private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);
	private static readonly object sync = new();

	// a probe must never go through the bypass itself, so it gets its own proxy-less client
	private static readonly HttpClient ProbeClient = new(new SocketsHttpHandler
	{
		UseProxy = false,
		AllowAutoRedirect = false,
		ConnectTimeout = TimeSpan.FromSeconds(4),
	})
	{ Timeout = TimeSpan.FromSeconds(6) };

	private static ConfBypass? conf;
	private static Process? proxyProcess;
	private static SocksBridge? bridge;
	private static Timer? supervisor;
	private static int sameResultCount;
	private static bool lastProbeDirect = true;

	/// <summary>How the bot's downloads leave the machine right now.</summary>
	public static Route Current { get; private set; } = Route.Unknown;

	/// <summary>One line for humans: what the bot is doing about the block right now.</summary>
	public static string Status => Current switch
	{
		Route.Direct => "YouTube открывается напрямую, обход не нужен",
		Route.Bypass => bridge is null
			? "обход включён, но мост не поднялся — смотри лог"
			: $"YouTube напрямую не открывается, работаю через обход (ByeDPI, порт {bridge.Port})",
		_ => conf is null || !conf.Enabled.Value ? "обход выключен в настройках" : "проверяю связь с YouTube...",
	};

	/// <summary>Decides the route before the first song plays, then keeps watching in the background.</summary>
	public static async Task StartAsync(ConfBypass config)
	{
		conf = config;
		if (!config.Enabled.Value)
		{
			Log.Info("bypass: disabled in the config, going out directly");
			Apply(Route.Direct);
			return;
		}

		if (config.Always.Value)
		{
			Log.Info("bypass: forced on by the config (tools.bypass.always)");
			Apply(Route.Bypass);
			return;
		}

		lastProbeDirect = await ProbeDirect();
		Apply(lastProbeDirect ? Route.Direct : Route.Bypass);
		supervisor = new Timer(_ => _ = SuperviseAsync(), null, ProbeInterval, ProbeInterval);
	}

	public static void Stop()
	{
		supervisor?.Dispose();
		supervisor = null;
		lock (sync)
		{
			bridge?.Dispose();
			bridge = null;
			StopProxyProcess();
		}
		ClearEnvironment();
		Current = Route.Unknown;
	}

	/// <summary>Re-checks right now instead of waiting for the next minute (used by the !net command).</summary>
	public static async Task<Route> RecheckAsync()
	{
		if (conf is null || !conf.Enabled.Value)
			return Current;
		if (conf.Always.Value)
			return Current;
		var direct = await ProbeDirect();
		sameResultCount = 0;
		lastProbeDirect = direct;
		Apply(direct ? Route.Direct : Route.Bypass);
		return Current;
	}

	/// <summary>Is YouTube reachable without any help?</summary>
	private static async Task<bool> ProbeDirect()
	{
		try
		{
			using var response = await ProbeClient.GetAsync("https://www.youtube.com/generate_204", HttpCompletionOption.ResponseHeadersRead);
			return (int)response.StatusCode < 400;
		}
		catch (Exception ex)
		{
			Log.Debug("bypass: youtube is not reachable directly ({0})", ex.Message);
			return false;
		}
	}

	/// <summary>
	/// Runs every minute: switches route only after two probes agree, so a single
	/// hiccup does not flip the bot back and forth.
	/// </summary>
	private static async Task SuperviseAsync()
	{
		try
		{
			EnsureProxyAlive();

			var direct = await ProbeDirect();
			sameResultCount = direct == lastProbeDirect ? sameResultCount + 1 : 0;
			lastProbeDirect = direct;

			var wanted = direct ? Route.Direct : Route.Bypass;
			if (wanted != Current && sameResultCount >= 1)
				Apply(wanted);
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "bypass: supervisor failed");
		}
	}

	private static void Apply(Route route)
	{
		lock (sync)
		{
			if (route == Current)
				return;

			if (route == Route.Bypass)
			{
				if (!StartBypass())
					return; // could not start it - stay where we are and try again next minute
				Log.Info("bypass: YouTube is blocked here, routing the bot's yt-dlp/ffmpeg through ByeDPI");
			}
			else
			{
				ClearEnvironment();
				// ciadpi and the bridge are left running on purpose: a song that is playing
				// right now still streams through them. They are shut down with the bot.
				Log.Info("bypass: YouTube opens directly, the bot goes straight out");
			}
			Current = route;
		}
	}

	private static bool StartBypass()
	{
		var endpoint = ParseEndpoint(conf!.Socks.Value, 1080);
		if (endpoint is null)
		{
			Log.Error("bypass: invalid socks endpoint '{0}'", conf.Socks.Value);
			return false;
		}

		if (proxyProcess is null)
			StartProxyProcess();

		if (bridge is null)
		{
			try
			{
				bridge = new SocksBridge(endpoint.Value.host, endpoint.Value.port, conf.BridgePort.Value);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "bypass: could not start the local HTTP bridge on port {0}", conf.BridgePort.Value);
				return false;
			}
		}

		var url = $"http://127.0.0.1:{bridge.Port}";
		foreach (var name in ProxyVars)
			System.Environment.SetEnvironmentVariable(name, url);
		// the web api and teamspeak itself are local/never blocked - keep them off the proxy
		System.Environment.SetEnvironmentVariable("no_proxy", "localhost,127.0.0.1,::1");
		System.Environment.SetEnvironmentVariable("NO_PROXY", "localhost,127.0.0.1,::1");
		return true;
	}

	private static void ClearEnvironment()
	{
		foreach (var name in ProxyVars)
			System.Environment.SetEnvironmentVariable(name, null);
	}

	private static void StartProxyProcess()
	{
		if (conf is null || string.IsNullOrWhiteSpace(conf.Path.Value))
			return; // no ciadpi configured: the 'socks' endpoint is expected to be someone else's
		var path = Path.GetFullPath(conf.Path.Value);
		if (!File.Exists(path))
		{
			Log.Error("bypass: ciadpi not found at {0}", path);
			return;
		}
		try
		{
			var process = new Process
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
			process.OutputDataReceived += (s, e) => { if (e.Data != null) Log.Debug("ciadpi: {0}", e.Data); };
			process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.Debug("ciadpi: {0}", e.Data); };
			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			proxyProcess = process;
			Log.Info("bypass: started ciadpi (pid {0})", process.Id);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "bypass: failed to start ciadpi");
		}
	}

	private static void StopProxyProcess()
	{
		var process = proxyProcess;
		proxyProcess = null;
		if (process is null)
			return;
		try { if (!process.HasExited) process.Kill(); }
		catch (Exception ex) { Log.Debug(ex, "bypass: kill failed"); }
		process.Dispose();
	}

	private static void EnsureProxyAlive()
	{
		lock (sync)
		{
			var process = proxyProcess;
			if (process is null || string.IsNullOrWhiteSpace(conf?.Path.Value))
				return;
			bool exited;
			try { exited = process.HasExited; }
			catch { exited = true; }
			if (!exited)
				return;
			Log.Warn("bypass: ciadpi died, restarting it");
			proxyProcess = null;
			StartProxyProcess();
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

/// <summary>
/// Minimal HTTP CONNECT proxy that forwards every tunnel to a SOCKS5 proxy.
/// Exists because ffmpeg speaks HTTP proxies but not SOCKS; host names are passed through
/// so the SOCKS side resolves them (a blocked DNS answer would defeat the whole point).
/// </summary>
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
				var socksStream = socks.GetStream();

				await socksStream.WriteAsync(new byte[] { 5, 1, 0 }, cts.Token);
				var greeting = new byte[2];
				await ReadExact(socksStream, greeting, 2);
				if (greeting[0] != 5 || greeting[1] != 0)
				{
					await Write(stream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n");
					return;
				}

				await socksStream.WriteAsync(BuildConnectRequest(host, port), cts.Token);

				var reply = new byte[4];
				await ReadExact(socksStream, reply, 4);
				if (reply[1] != 0)
				{
					Log.Debug("bridge: socks connect to {0}:{1} failed with code {2}", host, port, reply[1]);
					await Write(stream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n");
					return;
				}
				int skip = reply[3] switch { 1 => 4, 4 => 16, 3 => -1, _ => 0 };
				if (skip == -1)
				{
					var lengthByte = new byte[1];
					await ReadExact(socksStream, lengthByte, 1);
					skip = lengthByte[0];
				}
				var boundAddress = new byte[skip + 2];
				await ReadExact(socksStream, boundAddress, boundAddress.Length);

				await Write(stream, "HTTP/1.1 200 Connection established\r\n\r\n");
				if (len > headEnd)
					await socksStream.WriteAsync(buf.AsMemory(headEnd, len - headEnd), cts.Token);

				var up = stream.CopyToAsync(socksStream, 64 * 1024, cts.Token);
				var down = socksStream.CopyToAsync(stream, 64 * 1024, cts.Token);
				await Task.WhenAny(up, down);
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

	private static byte[] BuildConnectRequest(string host, int port)
	{
		byte[] request;
		if (IPAddress.TryParse(host, out var ip))
		{
			var address = ip.GetAddressBytes();
			request = new byte[4 + address.Length + 2];
			request[0] = 5; request[1] = 1; request[2] = 0; request[3] = (byte)(address.Length == 4 ? 1 : 4);
			address.CopyTo(request, 4);
		}
		else
		{
			var name = Encoding.ASCII.GetBytes(host);
			request = new byte[5 + name.Length + 2];
			request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3; request[4] = (byte)name.Length;
			name.CopyTo(request, 5);
		}
		request[^2] = (byte)(port >> 8);
		request[^1] = (byte)port;
		return request;
	}

	private static int FindHeaderEnd(byte[] buf, int len)
	{
		for (int i = 3; i < len; i++)
			if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n')
				return i + 1;
		return -1;
	}

	private async Task ReadExact(NetworkStream stream, byte[] buf, int count)
	{
		int offset = 0;
		while (offset < count)
		{
			int n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), cts.Token);
			if (n <= 0) throw new IOException("socks stream closed");
			offset += n;
		}
	}

	private async Task Write(NetworkStream stream, string text)
		=> await stream.WriteAsync(Encoding.ASCII.GetBytes(text), cts.Token);

	public void Dispose()
	{
		cts.Cancel();
		try { listener.Stop(); } catch (Exception ex) { Log.Debug(ex, "bridge: listener stop failed"); }
		cts.Dispose();
	}
}
