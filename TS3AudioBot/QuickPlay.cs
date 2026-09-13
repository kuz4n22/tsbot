// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TS3AudioBot;

/// <summary>
/// Local convenience layer ("TSBot" build):
///  - a bare media link pasted into chat is treated as "!yt &lt;link&gt;"
///  - plain text passed to play/add is resolved via a YouTube search
/// </summary>
public static partial class QuickPlay
{
	[GeneratedRegex(@"https?://[^\s\[\]<>""']+", RegexOptions.IgnoreCase)]
	private static partial Regex UrlRx();

	[GeneratedRegex(@"^(?:[a-z][a-z0-9+.-]*://|www\.|[a-z0-9-]+(?:\.[a-z0-9-]+)*\.[a-z]{2,}(?:[/:?#]|$))", RegexOptions.IgnoreCase)]
	private static partial Regex LinkLikeRx();

	[GeneratedRegex(@"[?&]list=([\w\-]+)", RegexOptions.IgnoreCase)]
	private static partial Regex PlaylistRx();

	/// <summary>Hosts for which a bare link in chat triggers playback.</summary>
	private static readonly string[] MediaHosts =
	[
		"youtube.com", "youtu.be", "youtube-nocookie.com",
		"soundcloud.com", "bandcamp.com", "twitch.tv",
	];

	/// <summary>Extracts the first http(s) link from a chat message.</summary>
	public static string? ExtractFirstLink(string message)
	{
		var m = UrlRx().Match(message);
		if (!m.Success)
			return null;
		return m.Value.TrimEnd('.', ',', ';', ')', '!', '?');
	}

	public static bool IsMediaHost(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			return false;
		var host = uri.Host.ToLowerInvariant();
		return MediaHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
	}

	/// <summary>
	/// If the message is not a command but contains a link to a known media site,
	/// returns the equivalent "!yt &lt;link&gt;" command; otherwise null.
	/// </summary>
	public static string? TryRewriteBareLink(string message)
	{
		var link = ExtractFirstLink(message);
		if (link is null || !IsMediaHost(link))
			return null;
		return "!yt " + link;
	}

	public static bool IsPlaylistLink(string url) => PlaylistRx().IsMatch(url);

	/// <summary>For "https://www.youtube.com/results?search_query=..." links returns the decoded query, otherwise null.</summary>
	public static string? SearchQueryFromYoutubeUrl(string? url)
	{
		if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsMediaHost(url))
			return null;
		if (!uri.AbsolutePath.StartsWith("/results", StringComparison.OrdinalIgnoreCase))
			return null;
		var q = System.Web.HttpUtility.ParseQueryString(uri.Query)["search_query"];
		return string.IsNullOrWhiteSpace(q) ? null : q.Trim();
	}

	/// <summary>True when the text is a url, a domain, or a file/directory path (i.e. NOT free text to search for).</summary>
	public static bool LooksLikeLinkOrPath(string text)
	{
		text = text.Trim();
		if (text.Length == 0)
			return true;
		if (LinkLikeRx().IsMatch(text))
			return true;
		if (text.StartsWith('/') || text.StartsWith('\\') || text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith(".\\", StringComparison.Ordinal))
			return true;
		if (text.Length > 2 && char.IsLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/'))
			return true;
		try
		{
			if (File.Exists(text) || Directory.Exists(text))
				return true;
		}
		catch (Exception) { /* invalid path chars etc. */ }
		return false;
	}
}
