// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

using System;

namespace TS3AudioBot.CommandSystem.Text;

public record struct TextMod(TextModFlag Flags, Color? HasColor = null) : IEquatable<TextMod>
{
	public static readonly TextMod None = new(0, null);

	public readonly TextMod Color(Color color) => new(Flags | TextModFlag.Color, color);
	public readonly TextMod Bold() => new(Flags | TextModFlag.Bold, HasColor);
	public readonly TextMod Italic() => new(Flags | TextModFlag.Italic, HasColor);
	public readonly TextMod Strike() => new(Flags | TextModFlag.Strike, HasColor);
	public readonly TextMod Underline() => new(Flags | TextModFlag.Underline, HasColor);

	public static string Format(AppliedTextMod format, params AppliedTextMod[] para)
		=> new TextModBuilder().AppendFormat(format, para).ToString();

	public static string Format(bool color, AppliedTextMod format, params AppliedTextMod[] para)
	{
		if (color)
			return Format(format, para);
		if (string.IsNullOrEmpty(format.Text))
			return string.Empty;
		// no-color: string.Format needs object[]; passing AppliedTextMod[] makes {0} print the array type. Use each mod's plain text.
		var args = new object?[para.Length];
		for (int i = 0; i < para.Length; i++)
			args[i] = para[i].Text;
		return string.Format(format.Text, args);
	}

	public readonly bool Equals(TextMod other) => Flags == other.Flags && HasColor == other.HasColor;
	public override readonly int GetHashCode() => ((int)Flags << 28) | HasColor.GetHashCode();
}
