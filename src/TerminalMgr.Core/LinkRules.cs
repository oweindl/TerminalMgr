namespace TerminalMgr.Core;

/// <summary>
/// Which addresses from the terminal output may be opened. The output is remote text, so the host does not trust the page:
/// only plain http and https addresses, no control characters, no file paths, no other schemes.
/// </summary>
public static class LinkRules
{
    public const int MaxLength = 2048;

    public static bool TryNormalize(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return false;
        string trimmed = text.Trim();
        if (trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(parsed.Host)) return false;
        uri = parsed;
        return true;
    }
}
