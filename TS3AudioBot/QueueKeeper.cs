// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TS3AudioBot.Audio;
using TS3AudioBot.Config;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TSLib.Scheduler;

namespace TS3AudioBot;

/// <summary>
/// TSBot addition: keeps the play queue on disk (bots/&lt;name&gt;/queue.json) and restores it
/// after a restart, resuming the song that was playing at roughly the same position.
/// </summary>
public sealed class QueueKeeper : IDisposable
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
	private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

	private readonly PlayManager playManager;
	private readonly PlaylistManager playlistManager;
	private readonly Player player;
	private readonly DedicatedTaskScheduler scheduler;
	private readonly string? file;
	private TickWorker? ticker;
	private string? lastWritten;
	private bool restored;

	private sealed class Snapshot
	{
		public List<Item> Items { get; set; } = [];
		public int Index { get; set; }
		public bool Playing { get; set; }
		public bool Paused { get; set; }
		public double Position { get; set; }
		public DateTime SavedAt { get; set; }
	}

	private sealed class Item
	{
		public string Type { get; set; } = "";
		public string Id { get; set; } = "";
		public string? Title { get; set; }
		public Dictionary<string, string>? Data { get; set; }
	}

	public QueueKeeper(ConfBot config, PlayManager playManager, PlaylistManager playlistManager, Player player, DedicatedTaskScheduler scheduler)
	{
		this.playManager = playManager;
		this.playlistManager = playlistManager;
		this.player = player;
		this.scheduler = scheduler;
		var dir = config.LocalConfigDir;
		file = dir is null ? null : Path.Combine(dir, "queue.json");
	}

	/// <summary>Saves every 5 s on the bot's own scheduler thread (no cross-thread access to the playlist).</summary>
	public void Start()
	{
		if (file is null || ticker != null)
			return;
		ticker = scheduler.CreateTimer(Save, TimeSpan.FromSeconds(5), true);
	}

	private Snapshot Capture()
	{
		var snap = new Snapshot
		{
			Index = playlistManager.Index,
			Playing = playManager.IsPlaying,
			Paused = player.Paused,
			Position = player.Position?.TotalSeconds ?? 0,
			SavedAt = DateTime.UtcNow,
		};
		foreach (var item in playlistManager.CurrentList.Items)
		{
			var ar = item.AudioResource;
			snap.Items.Add(new Item { Type = ar.AudioType, Id = ar.ResourceId, Title = ar.ResourceTitle, Data = ar.AdditionalData });
		}
		return snap;
	}

	public void Save()
	{
		if (file is null)
			return;
		try
		{
			var snap = Capture();
			// compare everything except the timestamp / fine position so we don't rewrite every tick for nothing
			var key = JsonSerializer.Serialize(new { snap.Items, snap.Index, snap.Playing, snap.Paused, Pos = (int)(snap.Position / 10) }, JsonOpts);
			if (key == lastWritten)
				return;
			var json = JsonSerializer.Serialize(snap, JsonOpts);
			var tmp = file + ".tmp";
			File.WriteAllText(tmp, json);
			File.Move(tmp, file, true);
			lastWritten = key;
		}
		catch (Exception ex)
		{
			Log.Debug(ex, "queue: save failed");
		}
	}

	/// <summary>Restores the queue from disk once per process lifetime (called when the bot is connected).</summary>
	public async Task RestoreAsync(InvokerData invoker)
	{
		if (file is null || restored)
			return;
		restored = true;
		if (!File.Exists(file))
			return;

		Snapshot? snap;
		try { snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file), JsonOpts); }
		catch (Exception ex)
		{
			Log.Warn(ex, "queue: could not read {0}", file);
			return;
		}
		if (snap is null || snap.Items.Count == 0)
			return;
		if (playlistManager.CurrentList.Items.Count > 0)
		{
			Log.Info("queue: not restoring, something is already queued");
			return;
		}

		var items = snap.Items
			.Where(i => !string.IsNullOrEmpty(i.Type) && !string.IsNullOrEmpty(i.Id))
			.Select(i => new PlaylistItem(new AudioResource(i.Id, i.Title, i.Type, i.Data)))
			.ToList();
		if (items.Count == 0)
			return;

		var index = Math.Clamp(snap.Index, 0, items.Count - 1);
		// resume inside the track by starting ffmpeg directly at the offset (no mid-stream seek needed)
		if (snap.Playing && snap.Position > 8)
			items[index].PlayInfo = new PlayInfo(TimeSpan.FromSeconds(snap.Position - 3));

		playlistManager.Clear();
		playlistManager.Queue(items);
		playlistManager.Index = index;
		Log.Info("queue: restored {0} item(s), index {1}, playing={2}, position={3:0}s", items.Count, index, snap.Playing, snap.Position);

		if (!snap.Playing)
			return;

		try
		{
			await playManager.Play(invoker);
		}
		catch (AudioBotException ex)
		{
			Log.Warn("queue: could not resume playback: {0}", ex.Message);
			return;
		}
		finally
		{
			// the offset is for this one resume only - without this the song would jump back there
			// every time it is played again (a skip that wraps around, a repeat, a later restart)
			items[index].PlayInfo = null;
		}
		if (snap.Paused)
			player.Paused = true;
	}

	public void Dispose()
	{
		ticker?.Disable();
		ticker = null;
		Save();
	}
}
