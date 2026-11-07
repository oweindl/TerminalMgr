namespace TerminalMgr.Windows;

/// <summary>The Makyx dashboard token (mkl_…) in the Windows Credential Manager of the current user. Never written to settings.json, never part of the configuration export, never logged.</summary>
public static class MakyxCredentialStore
{
    private const string Target = "TerminalMgr/Makyx/Leitstand";
    public static void Save(string token)
    {
        token = token.Trim();
        if (!token.StartsWith("mkl_", StringComparison.Ordinal) || token.Length > 100) throw new ArgumentException("Das ist kein Makyx-Token (es beginnt mit mkl_).");
        AiCredentialStore.Save(Target, "Makyx", token, "Ungültiges Token.");
    }
    public static string? Read() => AiCredentialStore.Read(Target);
    public static void Delete() => AiCredentialStore.Delete(Target);
    /// <summary>First characters for display ("mkl_a1b2…"); the rest is never shown.</summary>
    public static string? Hint() => Read() is { Length: > 8 } t ? t[..8] + "…" : null;
}
