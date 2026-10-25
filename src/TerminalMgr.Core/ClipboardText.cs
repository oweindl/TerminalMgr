using System.Text;

namespace TerminalMgr.Core;

/// <summary>What the clipboard list shows for one entry: at most two lines plus a size hint.</summary>
public sealed record ClipPreview(string Line1, string? Line2, string LengthLabel);

public static class ClipboardText
{
    /// <summary>Removes the padding spaces a terminal adds at the end of each line; normalizes line breaks to \n.</summary>
    public static string TrimTrailingSpaces(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd(' ')));

    public static ClipPreview Preview(string text, int maxLineChars = 120)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int lineCount = lines.Length;
        bool moreLines = lineCount > 2;
        string Cut(string line, bool forceEllipsis, out bool cut)
        {
            cut = line.Length > maxLineChars;
            string shown = cut ? line[..maxLineChars] : line;
            return cut || forceEllipsis ? shown.TrimEnd() + " …" : shown;
        }
        string first = Cut(lines[0], false, out bool firstCut);
        string? line2 = null;
        bool secondCut = false;
        if (lineCount > 1) line2 = Cut(lines[1], moreLines, out secondCut);
        string label = "";
        if (moreLines || firstCut || secondCut)
            label = lineCount > 1 ? $"{lineCount} Zeilen" : $"{text.Length} Zeichen";
        return new(first, line2, label);
    }

    /// <summary>A larger excerpt for the tooltip.</summary>
    public static string Excerpt(string text, int maxChars = 1200, int maxLines = 20)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var shown = string.Join('\n', lines.Take(maxLines));
        bool cut = lines.Length > maxLines || shown.Length > maxChars;
        if (shown.Length > maxChars) shown = shown[..maxChars];
        return cut ? shown + "\n…" : shown;
    }

    /// <summary>Control characters except line break and tab never reach the terminal as pasted text.</summary>
    public static string StripControlCharacters(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
            if (c is '\n' or '\t' || (!char.IsControl(c) && c != (char)0x2028 && c != (char)0x2029)) result.Append(c);
        return result.ToString();
    }
}

/// <summary>Decides whether a change of the Windows clipboard enters the list.</summary>
public static class ClipboardCapturePolicy
{
    /// <param name="excludeFormatPresent">Format ExcludeClipboardContentFromMonitorProcessing exists.</param>
    /// <param name="canIncludeInHistory">DWORD of CanIncludeInClipboardHistory, null when absent.</param>
    public static bool ShouldCapture(bool hasText, bool excludeFormatPresent, int? canIncludeInHistory, bool writtenByThisApp) =>
        hasText && !excludeFormatPresent && canIncludeInHistory != 0 && !writtenByThisApp;
}
