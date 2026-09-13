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
using TS3AudioBot.Config;
using TS3AudioBot.Helper;
using TS3AudioBot.Localization;
using TS3AudioBot.Playlists;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Web.Api;
using TS3AudioBot.Web.Model;
using TSLib;
using TSLib.Full.Book;

namespace TS3AudioBot;

// "TSBot" convenience commands (local build additions).
public static partial class MainCommands
{
	private static readonly NLog.Logger QuickLog = NLog.LogManager.GetCurrentClassLogger();

	[Command("yt")]
	[Usage("<link | playlist link | search text>", "Plays right away when idle, otherwise appends to the queue. Plain text is searched on YouTube. The bot follows you into your channel.")]
	public static async Task<string> CommandYt(PlayManager playManager, PlaylistManager playlistManager, ResolveContext resolver, Ts3Client ts3Client, Connection book, ConfBot config, InvokerData invoker, string text, ClientCall? clientCall = null)
	{
		text = (text ?? "").Trim();
		if (text.Length == 0)
			throw new CommandException("Использование: !yt <ссылка на видео/плейлист или текст для поиска>", CommandExceptionReason.CommandError);

		await FollowInvoker(ts3Client, book, config, clientCall);

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
		var ahead = playManager.RequestsAhead; // radio filler is dropped when a request comes in, so it does not count

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

	/// <summary>Skip = drop the current track: the next one, or the autoplay radio when the queue is empty (upstream "next" just errors there).</summary>
	internal static async Task<string> SkipCurrent(PlayManager playManager, InvokerData invoker)
	{
		if (!playManager.IsPlaying)
			throw new CommandException("Сейчас ничего не играет", CommandExceptionReason.CommandError);
		if (!await playManager.NextOrAutoplay(invoker))
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

	[Command("home")]
	[Usage("[<password>]", "Makes your current channel the bot's home: moves there now and remembers the channel (and its password) for the next start.")]
	public static async Task<string> CommandHome(Ts3Client ts3Client, Connection book, ConfBot config, ClientCall? clientCall = null, string? password = null)
	{
		if (clientCall?.ChannelId is not { } target)
			throw new CommandException(strings.error_no_target_channel, CommandExceptionReason.CommandError);
		if (!book.Channels.TryGetValue(target, out var channel))
			throw new CommandException("Не вижу твой канал — попробуй ещё раз через пару секунд", CommandExceptionReason.CommandError);

		// channel path the way the config wants it: "Parent/Child", a literal '/' in a name escaped as "\/"
		var parts = new System.Collections.Generic.List<string>();
		var cur = channel;
		while (cur != null)
		{
			parts.Insert(0, cur.Name.Replace("/", "\\/"));
			cur = cur.Parent != ChannelId.Null && book.Channels.TryGetValue(cur.Parent, out var parent) ? parent : null;
		}
		var path = string.Join("/", parts);

		if (book.OwnClient?.Channel != target)
			await ts3Client.MoveTo(target, password); // a wrong password surfaces here, before anything is saved

		config.Connect.Channel.Value = path;
		var pw = config.Connect.ChannelPassword;
		pw.Password.Value = password ?? "";
		pw.Hashed.Value = false;
		config.SaveWhenExists().UnwrapThrow();
		return string.IsNullOrEmpty(password) ? $"🏠 Теперь мой дом — «{channel.Name}»" : $"🏠 Теперь мой дом — «{channel.Name}», пароль запомнил";
	}

	[Command("net")]
	[Usage("", "Says how the bot reaches YouTube right now (direct or through the bypass) and checks again.")]
	public static async Task<string> CommandNet()
	{
		await NetworkBypass.RecheckAsync();
		return "🌐 " + NetworkBypass.Status;
	}

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
		 + "• !volume 20 — громкость (0–100)\n"
		 + "• !home [пароль] — сделать твой канал моим домом";

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

	private static async Task FollowInvoker(Ts3Client ts3Client, Connection book, ConfBot config, ClientCall? clientCall)
	{
		if (clientCall?.ChannelId is not { } target)
			return;
		var own = book.OwnClient?.Channel;
		if (own == target)
			return;
		// The configured room password gets the bot back into the room after it was re-created
		// (temporary channels vanish when empty); channels without a password ignore it.
		var pw = config.Connect.ChannelPassword;
		var password = !pw.Hashed.Value && !string.IsNullOrEmpty(pw.Password.Value) ? pw.Password.Value : null;
		try
		{
			await ts3Client.MoveTo(target, password);
		}
		catch (AudioBotException) when (password != null)
		{
			try
			{
				await ts3Client.MoveTo(target);
			}
			catch (AudioBotException ex)
			{
				QuickLog.Info("Could not follow {0} into channel {1}: {2}", clientCall.NickName, target, ex.Message);
			}
		}
		catch (AudioBotException ex)
		{
			QuickLog.Info("Could not follow {0} into channel {1}: {2}", clientCall.NickName, target, ex.Message);
		}
	}
}
