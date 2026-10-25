using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using TerminalMgr.Core;

namespace TerminalMgr.Windows;

/// <summary>Owns one Windows pseudoconsole. Input, output and teardown use separate workers.</summary>
public sealed class PtySession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private IntPtr _console;
    private IntPtr _process;
    private FileStream? _writer;
    private FileStream? _reader;
    private Task _readTask = Task.CompletedTask;
    private Task _writeTask = Task.CompletedTask;
    private Task _exitTask = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _stopping;
    public event Action<string>? Output;
    public event Action<int>? Exited;
    public event Action<string>? Error;

    public void Start(string executable, IEnumerable<string> arguments, int columns, int rows)
    {
        lock (_gate)
        {
            if (_console != IntPtr.Zero || _stopping) throw new InvalidOperationException("Session bereits gestartet.");
            IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
            IntPtr attributes = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;
            bool attributesInitialized = false;
            try
            {
                Check(Native.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
                Check(Native.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));
                Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(Size(columns, rows), inputRead, outputWrite, 0, out _console));
                nuint bytes = 0;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                attributes = Marshal.AllocHGlobal(checked((int)bytes));
                Check(Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
                attributesInitialized = true;
                Check(Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016,
                    _console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                var startup = new Native.StartupInfoEx { AttributeList = attributes };
                startup.StartupInfo.Size = Marshal.SizeOf<Native.StartupInfoEx>();
                startup.StartupInfo.Flags = 0x00000100; // STARTF_USESTDHANDLES: prevent inherited redirected stdio.
                environment = CreateEnvironmentBlock();
                var command = new StringBuilder(SshCommand.CommandLine(executable, arguments));
                Check(Native.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                    0x00080400, environment, null, ref startup, out var process));
                _process = process.Process;
                Close(ref process.Thread);
                _reader = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read, 4096, false);
                outputRead = IntPtr.Zero;
                _writer = new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write, 4096, false);
                inputWrite = IntPtr.Zero;
                _readTask = Task.Run(ReadOutput);
                _writeTask = Task.Run(WriteInput);
                _exitTask = Task.Run(() =>
                {
                    Native.WaitForSingleObject(_process, uint.MaxValue);
                    Native.GetExitCodeProcess(_process, out uint code);
                    Exited?.Invoke(unchecked((int)code));
                });
            }
            catch
            {
                if (_process != IntPtr.Zero) Native.TerminateProcess(_process, 1);
                // Drain any pending frame before closing a failed console creation.
                if (_console != IntPtr.Zero)
                {
                    var failedConsole = _console;
                    _console = IntPtr.Zero;
                    if (outputRead != IntPtr.Zero)
                    {
                        var drain = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read);
                        outputRead = IntPtr.Zero;
                        _ = Task.Run(() => { using (drain) { try { drain.CopyTo(Stream.Null); } catch (IOException) { } } });
                    }
                    Native.ClosePseudoConsole(failedConsole);
                }
                Close(ref _process);
                throw;
            }
            finally
            {
                Close(ref inputRead); Close(ref outputWrite); Close(ref inputWrite); Close(ref outputRead);
                if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            }
        }
    }

    public async ValueTask SendAsync(string data)
    {
        if (_stopping) return;
        try { await _input.Writer.WriteAsync(Encoding.UTF8.GetBytes(data)); }
        catch (ChannelClosedException) { }
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
            if (!_stopping && _console != IntPtr.Zero)
                Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(_console, Size(columns, rows)));
    }

    private void ReadOutput()
    {
        try
        {
            using var reader = new StreamReader(_reader!, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var buffer = new char[8192];
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                Output?.Invoke(new string(buffer, 0, count));
        }
        catch (IOException ex) { if (!_stopping) Error?.Invoke(ex.Message); }
        catch (ObjectDisposedException) when (_stopping) { }
    }

    private async Task WriteInput()
    {
        try
        {
            await foreach (byte[] bytes in _input.Reader.ReadAllAsync())
            {
                _writer!.Write(bytes);
                _writer.Flush();
            }
        }
        catch (IOException ex) { if (!_stopping) Error?.Invoke(ex.Message); }
        catch (ObjectDisposedException) when (_stopping) { }
        finally { _input.Writer.TryComplete(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= Task.Run(CloseAsync);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task CloseAsync()
    {
        IntPtr console;
        lock (_gate)
        {
            _stopping = true;
            console = _console;
            _console = IntPtr.Zero;
        }
        _input.Writer.TryComplete();
        if (_process != IntPtr.Zero) Native.TerminateProcess(_process, 0);
        // Keep the output reader alive until ClosePseudoConsole has emitted its final frame.
        if (console != IntPtr.Zero) Native.ClosePseudoConsole(console);
        await Task.WhenAll(_readTask, _writeTask, _exitTask);
        _reader?.Dispose();
        _writer?.Dispose();
        Close(ref _process);
    }

    private static IntPtr CreateEnvironmentBlock()
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            values[(string)entry.Key] = (string?)entry.Value ?? "";
        values["TERM"] = "xterm-256color";
        values["COLORTERM"] = "truecolor";
        string block = string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }
    private static Native.Coord Size(int cols, int rows) =>
        new() { X = (short)Math.Clamp(cols, 2, 1000), Y = (short)Math.Clamp(rows, 2, 1000) };
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static void Close(ref IntPtr handle) { if (handle != IntPtr.Zero) Native.CloseHandle(handle); handle = IntPtr.Zero; }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Coord { internal short X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal int Size;
            internal string? Reserved, Desktop, Title;
            internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal short ShowWindow, Reserved2Size;
            internal IntPtr Reserved2, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr security, uint size);
        [DllImport("kernel32.dll")] internal static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] internal static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll")] internal static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] internal static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(IntPtr process, uint code);
    }
}
