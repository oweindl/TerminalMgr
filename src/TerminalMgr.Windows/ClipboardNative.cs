using System.Runtime.InteropServices;

namespace TerminalMgr.Windows;

/// <summary>What one look at the Windows clipboard found. Text is null when no text format is present.</summary>
public sealed record ClipboardSnapshot(string? Text, bool ExcludeFormatPresent, int? CanIncludeInHistory);

/// <summary>Clipboard change notifications (WM_CLIPBOARDUPDATE) and a single-open snapshot read.</summary>
public static class ClipboardNative
{
    public const int WmClipboardUpdate = 0x031D;
    private const uint CfUnicodeText = 13;
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeFormat = "CanIncludeInClipboardHistory";

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);

    public static bool AddListener(IntPtr window) => AddClipboardFormatListener(window);
    public static void RemoveListener(IntPtr window) => RemoveClipboardFormatListener(window);
    public static uint SequenceNumber => GetClipboardSequenceNumber();

    /// <summary>Reads text and the password-manager markers while holding the clipboard once. Null when the clipboard stays busy.</summary>
    public static ClipboardSnapshot? Snapshot(IntPtr owner)
    {
        uint exclude = RegisterClipboardFormatW(ExcludeFormat);
        uint canInclude = RegisterClipboardFormatW(CanIncludeFormat);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(owner))
            {
                try
                {
                    bool excluded = exclude != 0 && IsClipboardFormatAvailable(exclude);
                    int? include = canInclude != 0 && IsClipboardFormatAvailable(canInclude) ? ReadDword(GetClipboardData(canInclude)) : null;
                    string? text = IsClipboardFormatAvailable(CfUnicodeText) ? ReadText(GetClipboardData(CfUnicodeText)) : null;
                    return new(text, excluded, include);
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(15);
        }
        return null;
    }

    private static int? ReadDword(IntPtr handle)
    {
        if (handle == IntPtr.Zero || GlobalSize(handle) < 4) return null;
        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try { return Marshal.ReadInt32(pointer); }
        finally { GlobalUnlock(handle); }
    }

    private static string? ReadText(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            int maxChars = (int)Math.Min(int.MaxValue / 2, GlobalSize(handle).ToUInt64() / 2);
            int length = 0;
            while (length < maxChars && Marshal.ReadInt16(pointer, length * 2) != 0) length++;
            return Marshal.PtrToStringUni(pointer, length);
        }
        finally { GlobalUnlock(handle); }
    }
}
