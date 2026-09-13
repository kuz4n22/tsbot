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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Config;
using TS3AudioBot.Environment;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TSLib.Helper;

namespace TS3AudioBot.Audio;

/// <summary>Provides a interface for enqueuing, playing and registering song events.</summary>
public class PlayManager
{
	private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

	/// <summary>TSBot: guards against two autoplay fetches running at once.</summary>
	private bool autoplayFetching;

	/// <summary>TSBot: true when the playing song is the last one in the queue.</summary>
	private bool AtEndOfQueue => playlistManager.Index >= playlistManager.CurrentList.Items.Count - 1;

	/// <summary>TSBot: how many related tracks autoplay queues at a time (a new batch follows when they run out).</summary>
	private const int AutoplayBatchSize = 25;

	private readonly ConfBot confBot;
	private readonly Player playerConnection;
	private readonly PlaylistManager playlistManager;
	private readonly ResolveContext resourceResolver;
	private readonly Stats stats;

	public PlayInfoEventArgs? CurrentPlayData { get; private set; }
	public bool IsPlaying => CurrentPlayData != null;
	private CancellationTokenSource? playRequest = null;

	public event AsyncEventHandler<PlayInfoEventArgs>? OnResourceUpdated;
	public event AsyncEventHandler<PlayInfoEventArgs>? BeforeResourceStarted;
	public event AsyncEventHandler<PlayInfoEventArgs>? AfterResourceStarted;
	public event AsyncEventHandler<SongEndEventArgs>? ResourceStopped;
	public event AsyncEventHandler? PlaybackStopped;

	public PlayManager(ConfBot config, Player playerConnection, PlaylistManager playlistManager, ResolveContext resourceResolver, Stats stats)
	{
		confBot = config;
		this.playerConnection = playerConnection;
		this.playlistManager = playlistManager;
		this.resourceResolver = resourceResolver;
		this.stats = stats;
	}

	public Task Enqueue(InvokerData invoker, AudioResource ar, PlayInfo? meta = null) => Enqueue(invoker, new PlaylistItem(ar, meta));
	public async Task Enqueue(InvokerData invoker, string message, string? audioType = null, PlayInfo? meta = null)
	{
		PlayResource playResource;
		try { playResource = await resourceResolver.Load(message, CancellationToken.None, audioType); }
		catch
		{
			stats.TrackSongLoad(audioType, false, true);
			throw;
		}
		await Enqueue(invoker, PlaylistItem.From(playResource).MergeMeta(meta));
	}
	public Task Enqueue(InvokerData invoker, IEnumerable<PlaylistItem> items)
	{
		var startOff = playlistManager.CurrentList.Items.Count;
		playlistManager.Queue(items.Select(x => UpdateItem(x, invoker)));
		return PostEnqueue(invoker, startOff);
	}
	public Task Enqueue(InvokerData invoker, PlaylistItem item)
	{
		var startOff = playlistManager.CurrentList.Items.Count;
		playlistManager.Queue(UpdateItem(item, invoker));
		return PostEnqueue(invoker, startOff);
	}

	private static PlaylistItem UpdateItem(PlaylistItem item, InvokerData invoker)
	{
		item.PlayInfo ??= new PlayInfo();
		item.PlayInfo.ResourceOwnerUid = invoker.ClientUid;
		return item;
	}

	private async Task PostEnqueue(InvokerData invoker, int startIndex)
	{
		if (IsPlaying)
			return;
		playlistManager.Index = startIndex;
		await StartCurrent(invoker);
	}

	/// <summary>Tries to play the passed <see cref="AudioResource"/></summary>
	/// <param name="invoker">The invoker of this resource. Used for responses and association.</param>
	/// <param name="ar">The resource to load and play.</param>
	/// <param name="meta">Allows overriding certain settings for the resource. Can be null.</param>
	/// <returns>Ok if successful, or an error message otherwise.</returns>
	public async Task Play(InvokerData invoker, AudioResource ar, PlayInfo? meta = null)
	{
		ArgumentNullException.ThrowIfNull(ar);

		PlayResource playResource;
		try
		{
			playResource = await WithPlayRequestCancelToken(async ct => await resourceResolver.Load(ar, ct));
		}
		catch
		{
			stats.TrackSongLoad(ar.AudioType, false, true);
			throw;
		}
		await Play(invoker, playResource.MergeMeta(meta));
	}

	/// <summary>Tries to play the passed link.</summary>
	/// <param name="invoker">The invoker of this resource. Used for responses and association.</param>
	/// <param name="link">The link to resolve, load and play.</param>
	/// <param name="audioType">The associated resource type string to a factory.</param>
	/// <param name="meta">Allows overriding certain settings for the resource. Can be null.</param>
	/// <returns>Ok if successful, or an error message otherwise.</returns>
	public async Task Play(InvokerData invoker, string link, string? audioType = null, PlayInfo? meta = null)
	{
		PlayResource playResource;
		try
		{
			playResource = await WithPlayRequestCancelToken(async ct => await resourceResolver.Load(link, ct, audioType));
		}
		catch
		{
			stats.TrackSongLoad(audioType, false, true);
			throw;
		}
		await Play(invoker, playResource.MergeMeta(meta));
	}

	public async Task Play(InvokerData invoker, IEnumerable<PlaylistItem> items, int index = 0)
	{
		playlistManager.Clear();
		playlistManager.Queue(items.Select(x => UpdateItem(x, invoker)));
		playlistManager.Index = index;
		await StartCurrent(invoker);
	}

	public async Task Play(InvokerData invoker, PlaylistItem item)
	{
		ArgumentNullException.ThrowIfNull(item);

		if (item.AudioResource is null)
			throw new Exception("Invalid playlist item");
		playlistManager.Clear();
		playlistManager.Queue(item);
		playlistManager.Index = 0;
		await StartResource(invoker, item);
	}

	public Task Play(InvokerData invoker) => StartCurrent(invoker);

	/// <summary>Plays the passed <see cref="PlayResource"/></summary>
	/// <param name="invoker">The invoker of this resource. Used for responses and association.</param>
	/// <param name="play">The associated resource type string to a factory.</param>
	/// <param name="meta">Allows overriding certain settings for the resource.</param>
	/// <returns>Ok if successful, or an error message otherwise.</returns>
	public async Task Play(InvokerData invoker, PlayResource play)
	{
		playlistManager.Clear();
		playlistManager.Queue(PlaylistItem.From(play));
		playlistManager.Index = 0;
		stats.TrackSongLoad(play.AudioResource.AudioType, true, true);
		await StartResource(invoker, play);
	}

	private async Task StartCurrent(InvokerData invoker, bool manually = true)
	{
		var pli = playlistManager.Current;
		if (pli is null)
			throw Error.LocalStr(strings.error_playlist_is_empty);
		try
		{
			await StartResource(invoker, pli);
		}
		catch (AudioBotException ex)
		{
			Log.Warn("Skipping: {0} because {1}", pli, ex.Message);
			await Next(invoker, manually);
		}
	}

	private async Task StartResource(InvokerData invoker, PlaylistItem item)
	{
		PlayResource playResource;
		try
		{
			playResource = await WithPlayRequestCancelToken(async ct => await resourceResolver.Load(item.AudioResource, ct));
		}
		catch
		{
			stats.TrackSongLoad(item.AudioResource.AudioType, false, false);
			throw;
		}
		stats.TrackSongLoad(item.AudioResource.AudioType, true, false);
		await StartResource(invoker, playResource.MergeMeta(item.PlayInfo));
	}

	private async Task StartResource(InvokerData invoker, PlayResource play)
	{
		var sourceLink = resourceResolver.RestoreLink(play.AudioResource);
		var playInfo = new PlayInfoEventArgs(invoker, play, sourceLink);
		await BeforeResourceStarted.InvokeAsync(this, playInfo);

		if (string.IsNullOrWhiteSpace(play.PlayUri))
		{
			Log.Error("Internal resource error: link is empty (resource:{0})", play);
			throw Error.LocalStr(strings.error_playmgr_internal_error);
		}

		Log.Debug("AudioResource start: {0}", play);
		try { await playerConnection.Play(play); }
		catch (AudioBotException ex)
		{
			Log.Error("Error return from player: {0}", ex.Message);
			throw Error.Exception(ex).LocalStr(strings.error_playmgr_internal_error);
		}

		playerConnection.Volume = Tools.Clamp(playerConnection.Volume, confBot.Audio.Volume.Min, confBot.Audio.Volume.Max);
		CurrentPlayData = playInfo; // TODO meta as readonly
		await AfterResourceStarted.InvokeAsync(this, playInfo);
		_ = PrefetchAutoplay(); // TSBot: line up the next radio batch before this song ends
	}

	public async Task Next(InvokerData invoker, bool manually = true)
	{
		PlaylistItem? pli = null;
		for (int i = 0; i < 10; i++)
		{
			pli = playlistManager.Next(manually);
			if (pli is null) break;
			try
			{
				await StartResource(invoker, pli);
				return;
			}
			catch (AudioBotException ex) { Log.Warn("Skipping: {0} because {1}", pli, ex.Message); }
		}
		if (pli is null)
			throw Error.LocalStr(strings.info_playmgr_no_next_song);
		else
			throw Error.LocalStr(string.Format(strings.error_playmgr_many_songs_failed, "!next"));
	}

	public async Task Previous(InvokerData invoker, bool manually = true)
	{
		PlaylistItem? pli = null;
		for (int i = 0; i < 10; i++)
		{
			pli = playlistManager.Previous(manually);
			if (pli is null) break;
			try
			{
				await StartResource(invoker, pli);
				return;
			}
			catch (AudioBotException ex) { Log.Warn("Skipping: {0} because {1}", pli, ex.Message); }
		}
		if (pli is null)
			throw Error.LocalStr(strings.info_playmgr_no_previous_song);
		else
			throw Error.LocalStr(string.Format(strings.error_playmgr_many_songs_failed, "!previous"));
	}

	public async Task SongStoppedEvent(object? sender, EventArgs e) => await StopInternal(true);

	public Task Stop() => StopInternal(false);

	private async Task StopInternal(bool songEndedByCallback)
	{
		await ResourceStopped.InvokeAsync(this, new SongEndEventArgs(songEndedByCallback));

		if (songEndedByCallback)
		{
			// TSBot: same path as !skip - next song, or the autoplay radio when the queue is empty
			if (await NextOrAutoplay(CurrentPlayData?.Invoker ?? InvokerData.Anonymous, false))
				return;
		}
		else
		{
			playerConnection.Stop();
		}

		CurrentPlayData = null;
		PlaybackStopped?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// TSBot: the single way forward through the queue, used both when a song ends on its own
	/// and by !skip, so the two behave the same. Returns false only when the music cannot go on
	/// (nothing queued and autoplay off or unavailable).
	/// </summary>
	public async Task<bool> NextOrAutoplay(InvokerData invoker, bool manually = true)
	{
		// Upstream wraps a manual !next at the end of the queue back to the first song. With the
		// autoplay radio that is the wrong surprise: the queue is over, so move on rather than back.
		if (manually && AtEndOfQueue && playlistManager.Loop == LoopMode.Off && !playlistManager.Random
			&& await TryAutoplay())
			return true;

		try
		{
			await Next(invoker, manually);
			return true;
		}
		catch (AudioBotException ex)
		{
			Log.Info("Queue ran out: {0}", ex.Message);
			return await TryAutoplay();
		}
	}

	/// <summary>
	/// TSBot: takes the YouTube radio mix (RD&lt;id&gt;) of the song playing now and appends a small
	/// batch of it to the queue. Only tracks nobody has heard in this session are taken, so the
	/// music keeps drifting instead of looping the same list. Returns how many were added.
	/// </summary>
	private async Task<int> QueueAutoplayBatch()
	{
		var last = CurrentPlayData;
		var ar = last?.ResourceData;
		if (last is null || ar is null || ar.AudioType != "youtube" || string.IsNullOrEmpty(ar.ResourceId))
			return 0;

		var mixUrl = $"https://www.youtube.com/watch?v={ar.ResourceId}&list=RD{ar.ResourceId}";
		var plist = await resourceResolver.LoadPlaylistFrom(mixUrl, CancellationToken.None);
		var known = playlistManager.CurrentList.Items.Select(i => i.AudioResource.ResourceId).ToHashSet();
		var fresh = plist.Items
			.Where(i => !known.Contains(i.AudioResource.ResourceId))
			.Take(AutoplayBatchSize)
			.ToList();
		if (fresh.Count == 0)
		{
			Log.Info("Autoplay: the mix of {0} had nothing new left", ar.ResourceId);
			return 0;
		}

		playlistManager.Queue(fresh.Select(x => UpdateItem(x, last.Invoker)));
		Log.Info("Autoplay: queued {0} related track(s) from the mix of {1}", fresh.Count, ar.ResourceId);
		return fresh.Count;
	}

	/// <summary>
	/// TSBot: fetches the next batch while the last queued song is still playing, so the music
	/// never stops to wait for YouTube. Does nothing while there are still songs queued.
	/// </summary>
	private async Task PrefetchAutoplay()
	{
		if (!confBot.Audio.Autoplay || autoplayFetching || !AtEndOfQueue)
			return;
		autoplayFetching = true;
		try { await QueueAutoplayBatch(); }
		catch (Exception ex) { Log.Debug(ex, "Autoplay prefetch failed"); }
		finally { autoplayFetching = false; }
	}

	/// <summary>TSBot: the queue is empty - fill it from the radio mix and keep playing.</summary>
	private async Task<bool> TryAutoplay()
	{
		if (!confBot.Audio.Autoplay)
			return false;
		var invoker = CurrentPlayData?.Invoker;
		if (invoker is null)
			return false;
		try
		{
			var startOff = playlistManager.CurrentList.Items.Count;
			if (await QueueAutoplayBatch() == 0)
				return false;
			playlistManager.Index = startOff;
			await StartCurrent(invoker, false);
			return true;
		}
		catch (AudioBotException ex)
		{
			Log.Info("Autoplay failed: {0}", ex.Message);
			return false;
		}
	}

	public async Task Update(SongInfoChanged newInfo)
	{
		var data = CurrentPlayData;
		if (data is null)
			return;
		if (newInfo.Title != null)
			data.ResourceData.ResourceTitle = newInfo.Title;
		// further properties...
		try
		{
			await OnResourceUpdated.InvokeAsync(this, data);
		}
		catch (AudioBotException ex)
		{
			Log.Warn(ex, "Error in OnResourceUpdated event.");
		}
	}

	public static PlayInfo? ParseAttributes(string[] attrs)
	{
		if (attrs is null || attrs.Length == 0)
			return null;

		var meta = new PlayInfo();
		foreach (var attr in attrs)
		{
			if (attr.StartsWith('@'))
			{
				meta.StartOffset = TextUtil.ParseTime(attr[1..]);
			}
		}
		return meta;
	}

	private async Task<T> WithPlayRequestCancelToken<T>(Func<CancellationToken, Task<T>> request)
	{
		using var cts = new CancellationTokenSource();
		try
		{
			playRequest?.Cancel();
			playRequest = cts;
			T res = await request(cts.Token);
			return res;
		}
		finally
		{
			if (playRequest == cts) playRequest = null;
		}
	}
}
