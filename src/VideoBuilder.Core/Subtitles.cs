using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoBuilder.Core;

/// <summary>字幕 1 つ (開始・終了の時刻と本文)。</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>字幕ファイル (SRT / WebVTT) の読み書き。</summary>
public static class Subtitles
{
    public static readonly string[] Extensions = { "srt", "vtt" };

    private static readonly Regex TimeLine = new(
        @"^\s*(?<s>(?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*(?<e>(?:\d+:)?\d{1,2}:\d{2}[.,]\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // <i> <b> <c.xxx> <v 名前> <00:00:01.000> などのタグと、{\an8} のような ASS の指定
    private static readonly Regex Tags = new(@"<[^>]*>|\{\\[^}]*\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>SRT / VTT の中身を読む (どちらかは中身で判断する)。時刻の読めない字幕は飛ばす。</summary>
    public static List<SubtitleCue> Parse(string content)
    {
        var cues = new List<SubtitleCue>();
        var lines = content.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = TimeLine.Match(lines[i]);
            if (!m.Success) continue;
            if (ParseTime(m.Groups["s"].Value) is not { } start || ParseTime(m.Groups["e"].Value) is not { } end) continue;
            var text = new List<string>();
            int j = i + 1;
            for (; j < lines.Length && lines[j].Trim().Length > 0; j++)
            {
                if (TimeLine.IsMatch(lines[j])) break;   // 空行を挟まずに次の字幕が始まっている
                text.Add(Tags.Replace(lines[j], "").TrimEnd());
            }
            i = j - 1;
            var body = string.Join("\n", text).Trim('\n');
            if (end < start) (start, end) = (end, start);
            cues.Add(new SubtitleCue(start, end, System.Net.WebUtility.HtmlDecode(body)));
        }
        return cues;
    }

    private static TimeSpan? ParseTime(string s)
    {
        var parts = s.Replace(',', '.').Split(':');
        try
        {
            double sec = double.Parse(parts[^1], CultureInfo.InvariantCulture);
            int min = int.Parse(parts[^2], CultureInfo.InvariantCulture);
            int hour = parts.Length >= 3 ? int.Parse(parts[^3], CultureInfo.InvariantCulture) : 0;
            return TimeSpan.FromSeconds(hour * 3600 + min * 60 + sec);
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }

    private static string Format(TimeSpan t, char fraction)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        long ms = (long)Math.Round(t.TotalMilliseconds);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}{fraction}{ms % 1000:000}");
    }

    public static string ToSrt(IEnumerable<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        int n = 1;
        foreach (var c in cues)
        {
            sb.Append(n++).Append('\n');
            sb.Append(Format(c.Start, ',')).Append(" --> ").Append(Format(c.End, ',')).Append('\n');
            sb.Append(c.Text.Replace("\r\n", "\n")).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToVtt(IEnumerable<SubtitleCue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
        {
            sb.Append(Format(c.Start, '.')).Append(" --> ").Append(Format(c.End, '.')).Append('\n');
            // VTT では本文の空行が字幕の終わりになるので、空行は詰める
            var text = string.Join("\n", c.Text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0));
            sb.Append(text.Replace("-->", "->")).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>拡張子 (.vtt なら VTT、それ以外は SRT) で形式を決めて書く。</summary>
    public static string Write(string path, IEnumerable<SubtitleCue> cues) =>
        Path.GetExtension(path).Equals(".vtt", StringComparison.OrdinalIgnoreCase) ? ToVtt(cues) : ToSrt(cues);
}
