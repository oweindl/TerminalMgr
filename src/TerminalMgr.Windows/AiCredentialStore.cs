using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TerminalMgr.Windows;

/// <summary>Windows Credential Manager, scoped to the current Windows user. Never exported with settings.</summary>
public static class AiCredentialStore
{
    private const string Target = "TerminalMgr/AI/OpenRouter";
    public static void SaveOpenRouter(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 2048 || secret.Any(char.IsControl)) throw new ArgumentException("Ungültiger API-Key.");
        byte[] bytes = Encoding.UTF8.GetBytes(secret.Trim());
        IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Target, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = 2, UserName = "OpenRouter" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0); Marshal.FreeHGlobal(blob); Array.Clear(bytes); }
    }
    public static string? ReadOpenRouter()
    {
        if (!CredRead(Target, 1, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            byte[] bytes = new byte[checked((int)credential.CredentialBlobSize)];
            try { Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }
    public static void DeleteOpenRouter()
    {
        if (!CredDelete(Target, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
}
