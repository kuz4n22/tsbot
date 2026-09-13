// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.Threading;
using System.Threading.Tasks;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Api;
using TS3AudioBot.Web.Model;
using TSLib.Full.Book;

namespace TS3AudioBot;

// "TSBot" convenience commands (local build additions).
public static partial class MainCommands
{
	private static readonly NLog.Logger QuickLog = NLog.LogManager.GetCurrentClassLogger();

	[Command("yt")]
	[Usage("<link | playlist link | search text>", "Plays right away when idle, otherwise appends to the queue. Plain text is searched on YouTube. The bot follows you into your channel.")]
	public static async Task<string> CommandYt(PlayManager playManager, PlaylistManager playlistManager, ResolveContext resolver, Ts3Client ts3Client, Connection book, InvokerData invoker, string text, ClientCall? clientCall = null)
	{
		text = (text ?? "").Trim();
		if (text.Length == 0)
			throw new CommandException("Использование: !yt <ссылка на видео/плейлист или текст для поиска>", CommandExceptionReason.CommandError);

		await FollowInvoker(ts3Client, book, clientCall);

		// TS6 sends links as "[text](url)" or "[URL]url[/URL]"; pull the actual url out of whatever came
		var link = QuickPlay.ExtractFirstLink(text) ?? TextUtil.ExtractUrlFromBb(text);
		var searchQuery = QuickPlay.SearchQueryFromYoutubeUrl(link);
		if (searchQuery != null)
		{
			// a "youtube.com/results?search_query=..." link: search that text instead
			text = searchQuery;
			link = searchQuery;
		}
		else if (QuickPlay.LooksLikeLinkOrPath(link))
			text = link;

		var wasPlaying = playManager.IsPlaying;
		var ahead = Math.Max(0, playlistManager.CurrentList.Items.Count - playlistManager.Index - 1);

		if (QuickPlay.LooksLikeLinkOrPath(link) && QuickPlay.IsPlaylistLink(link))
		{
			var plist = await resolver.LoadPlaylistFrom(link, CancellationToken.None);
			if (plist.Items.Count == 0)
				throw new CommandException(strings.error_playlist_is_empty, CommandExceptionReason.CommandError);
			var title = string.IsNullOrWhiteSpace(plist.Title) ? "плейлист" : $"«{plist.Title}»";
			if (wasPlaying)
			{
				await playManager.Enqueue(invoker, plist.Items);
				return $"➕ {title}: {plist.Items.Count} трек(ов) в очередь (впереди ещё {ahead})";
			}
			await playManager.Play(invoker, plist.Items);
			return $"▶ {title}: {plist.Items.Count} трек(ов), поехали";
		}

		var playRes = await resolver.Load(text, CancellationToken.None);
		var songTitle = playRes.AudioResource.ResourceTitle ?? playRes.SongInfo?.Title ?? link;
		if (wasPlaying)
		{
			await playManager.Enqueue(invoker, PlaylistItem.From(playRes));
			return $"➕ В очередь (впереди {ahead}): {songTitle}";
		}
		await playManager.Play(invoker, playRes);
		return $"▶ {songTitle}";
	}

	[Command("skip")]
	public static async Task<string> CommandSkip(PlayManager playManager, InvokerData invoker)
		=> await SkipCurrent(playManager, invoker);

	/// <summary>Skip = drop the current track: play the next one if there is one, otherwise stop (upstream "next" just errors at the end of the queue).</summary>
	internal static async Task<string> SkipCurrent(PlayManager playManager, InvokerData invoker)
	{
		if (!playManager.IsPlaying)
			throw new CommandException("Сейчас ничего не играет", CommandExceptionReason.CommandError);
		try
		{
			await playManager.Next(invoker);
		}
		catch (AudioBotException ex) when (ex.Message == strings.info_playmgr_no_next_song)
		{
			await playManager.Stop();
			return "⏹ Очередь пуста — остановил";
		}
		var title = playManager.CurrentPlayData?.ResourceData.ResourceTitle;
		return string.IsNullOrEmpty(title) ? "⏭ Следующий" : $"⏭ {title}";
	}

	[Command("queue")]
	[Usage("[<count>]", "Shows the current queue around the playing song.")]
	public static JsonValue<QueueInfo> CommandQueue(ResolveContext resourceFactory, PlaylistManager playlistManager, int? count = null)
	{
		var start = Math.Max(0, playlistManager.Index - 1);
		return CommandInfo(resourceFactory, playlistManager, start, count ?? 10);
	}

	[Command("np")]
	public static JsonValue<CurrentSongInfo> CommandNowPlaying(PlayManager playManager, Player player, Bot bot, ClientCall? invoker = null)
		=> CommandSong(playManager, player, bot, invoker);

	[Command("commands")]
	public static string CommandCheatSheet()
		=> "🎧 DJ Bot — как заказать музыку\n"
		 + "• Кинь ссылку на YouTube в чат — включу или поставлю в очередь\n"
		 + "• !yt название — найти и включить\n"
		 + "• !skip — следующий трек\n"
		 + "• !pause / !play — пауза / дальше\n"
		 + "• !stop — стоп\n"
		 + "• !np — что играет\n"
		 + "• !queue — очередь · !clear — очистить\n"
		 + "• !volume 20 — громкость (0–100)";

	/// <summary>"!play never gonna give you up": glue the words back together unless the first token is a link/path.</summary>
	private static string JoinFreeText(string first, string[] rest)
	{
		if (rest is null || rest.Length == 0 || QuickPlay.LooksLikeLinkOrPath(first))
			return first;
		var words = new System.Collections.Generic.List<string> { first };
		foreach (var w in rest)
			if (!w.StartsWith('@'))
				words.Add(w);
		return string.Join(' ', words);
	}

	private static async Task FollowInvoker(Ts3Client ts3Client, Connection book, ClientCall? clientCall)
	{
		if (clientCall?.ChannelId is not { } target)
			return;
		var own = book.OwnClient?.Channel;
		if (own == target)
			return;
		try
		{
			await ts3Client.MoveTo(target);
		}
		catch (AudioBotException ex)
		{
			QuickLog.Info("Could not follow {0} into channel {1}: {2}", clientCall.NickName, target, ex.Message);
		}
	}
}
