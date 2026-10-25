using System.Text;

namespace TerminalMgr.Core;

/// <summary>
/// OSC 52 ("manipulate selection data"): <c>ESC ] 52 ; Pc ; Pd ST</c>. A program in the session, or tmux on its behalf, asks the
/// terminal to put text into the clipboard. Only write requests are honored; a read request (<c>Pd</c> = "?") never
/// gets an answer, so nothing in the session can read the clipboard.
/// </summary>
public static class Osc52
{
    private const string SelectionNames = "cpqs01234567";
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <param name="payload">What follows "52;" in the sequence, e.g. "c;SGVsbG8=".</param>
    /// <returns>True for a valid, non-empty write request that fits the clipboard list's size limit.</returns>
    public static bool TryDecode(string? payload, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(payload)) return false;
        int separator = payload.IndexOf(';');
        if (separator < 0) return false;
        string selection = payload[..separator];
        if (selection.Any(c => !SelectionNames.Contains(c))) return false;
        string data = new(payload[(separator + 1)..].Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (data.Length == 0 || data == "?") return false;   // empty = "clear" and "?" = read: neither is acted upon
        // Largest base64 text that can still decode to something within the limit (4 characters per 3 bytes).
        if (data.Length > ClipboardList.MaxTextLength * 4L / 3 + 8) return false;
        try
        {
            int padding = (4 - data.Length % 4) % 4;
            if (padding == 3) return false;
            var bytes = Convert.FromBase64String(data + new string('=', padding));
            string decoded = Strict.GetString(bytes);
            if (decoded.Length == 0 || decoded.Length > ClipboardList.MaxTextLength) return false;
            text = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return false; }
    }
}
