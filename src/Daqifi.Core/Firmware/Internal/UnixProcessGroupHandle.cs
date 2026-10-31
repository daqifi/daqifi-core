using System.Collections;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Daqifi.Core.Firmware.Internal;

/// <summary>
/// Starts a tool in its own POSIX process group. The group survives its launcher, so a timeout
/// can still terminate workers that inherited its output pipes after the launcher exited.
/// </summary>
internal sealed class UnixProcessGroupHandle : IProcessHandle
{
    private readonly int _pid;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private UnixProcessGroupHandle(int pid, Stream output, Stream error, Stream input, bool redirectInput)
    {
        _pid = pid;
        StandardOutput = new StreamReader(output);
        StandardError = new StreamReader(error);
        if (redirectInput)
        {
            StandardInput = new StreamWriter(input, new UTF8Encoding(false));
        }
        else
        {
            input.Dispose();
            StandardInput = TextWriter.Null;
        }

        // Reap this exact native child, without occupying a thread-pool worker for the run.
        new Thread(() =>
        {
            int result;
            int status;
            do
            {
                result = Native.waitpid(pid, out status, 0);
            }
            while (result < 0 && Marshal.GetLastWin32Error() == 4); // EINTR
            if (result == pid)
            {
                _exited.TrySetResult((status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F));
            }
            else
            {
                _exited.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }) { IsBackground = true, Name = "daqifi-process-exit" }.Start();
    }

    public bool HasExited => _exited.Task.IsCompletedSuccessfully;
    public int ExitCode => HasExited ? _exited.Task.GetAwaiter().GetResult()
        : throw new InvalidOperationException("The process has not exited.");
    public TextReader StandardOutput { get; }
    public TextReader StandardError { get; }
    public TextWriter StandardInput { get; }
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

    public void KillProcessTree()
    {
        if (Native.kill(-_pid, 9) != 0 && Marshal.GetLastWin32Error() != 3) // SIGKILL; ESRCH means gone
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
    }

    internal static IProcessHandle Start(ExternalProcessRequest request)
    {
        using var output = new SpawnPipe(parentReads: true);
        using var error = new SpawnPipe(parentReads: true);
        using var input = new SpawnPipe(parentReads: false);
        // The libc initializers own the representation of these opaque states. The storage is
        // deliberately larger than either supported libc's ABI; no native fields are read here.
        var actions = Marshal.AllocHGlobal(1024);
        var attributes = Marshal.AllocHGlobal(1024);
        var actionsInitialized = false;
        var attributesInitialized = false;
        try
        {
            Check(Native.posix_spawn_file_actions_init(actions));
            actionsInitialized = true;
            Check(Native.posix_spawnattr_init(attributes));
            attributesInitialized = true;
            Check(Native.posix_spawnattr_setpgroup(attributes, 0));
            Check(Native.posix_spawnattr_setflags(attributes, 2)); // POSIX_SPAWN_SETPGROUP
            input.Redirect(actions, 0);
            output.Redirect(actions, 1);
            error.Redirect(actions, 2);
            if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
            {
                Check(Native.posix_spawn_file_actions_addchdir_np(actions, request.WorkingDirectory));
            }

            using var argv = new NativeStrings([request.FileName, .. SplitArguments(request.Arguments ?? string.Empty)]);
            using var environment = new NativeStrings(Environment.GetEnvironmentVariables()
                .Cast<DictionaryEntry>().Select(entry => $"{entry.Key}={entry.Value}"));
            Check(Native.posix_spawnp(out var pid, ResolveExecutable(request.FileName), actions, attributes, argv.Pointer, environment.Pointer));
            try
            {
                return new UnixProcessGroupHandle(pid, output.TakeParent(), error.TakeParent(), input.TakeParent(),
                    request.StandardInputResponseFactory != null);
            }
            catch
            {
                _ = Native.kill(-pid, 9);
                _ = Native.waitpid(pid, out _, 0);
                throw;
            }
        }
        finally
        {
            if (actionsInitialized) _ = Native.posix_spawn_file_actions_destroy(actions);
            if (attributesInitialized) _ = Native.posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attributes);
        }
    }

    private static string ResolveExecutable(string fileName)
    {
        // As with Process.Start, WorkingDirectory changes the child's directory, not where
        // the parent resolves a relative executable path.
        if (fileName.Contains('/', StringComparison.Ordinal))
        {
            return Path.GetFullPath(fileName);
        }

        var directories = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator));
        foreach (var directory in directories)
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, fileName));
            if (Native.access(candidate, 1) == 0) return candidate; // X_OK
        }

        return fileName; // posix_spawnp reports the ordinary not-found/permission error.
    }

    // Match ProcessStartInfo.Arguments' C/C++ quote and backslash rules. No shell evaluates
    // these values; posix_spawnp receives each argument separately.
    internal static IReadOnlyList<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && text[position] is ' ' or '\t') position++;
            if (position == text.Length) break;
            var value = new StringBuilder();
            var quoted = false;
            while (position < text.Length)
            {
                var slashes = 0;
                while (position < text.Length && text[position] == '\\') { slashes++; position++; }
                if (slashes > 0)
                {
                    var beforeQuote = position < text.Length && text[position] == '"';
                    value.Append('\\', beforeQuote ? slashes / 2 : slashes);
                    if (beforeQuote && slashes % 2 != 0) { value.Append('"'); position++; }
                    continue;
                }

                var character = text[position];
                if (character == '"')
                {
                    if (quoted && position + 1 < text.Length && text[position + 1] == '"')
                    {
                        value.Append('"');
                        position += 2;
                    }
                    else { quoted = !quoted; position++; }
                }
                else if (!quoted && character is ' ' or '\t') break;
                else { value.Append(character); position++; }
            }

            result.Add(value.ToString());
        }

        return result;
    }

    private static void Check(int error)
    {
        if (error != 0) throw new Win32Exception(error);
    }

    private sealed class SpawnPipe : IDisposable
    {
        private SafePipeHandle? _parent;
        private readonly SafePipeHandle _child;
        private readonly PipeDirection _direction;
        internal SpawnPipe(bool parentReads)
        {
            var descriptors = new int[2];
            if (Native.pipe(descriptors) != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _parent = new SafePipeHandle(descriptors[parentReads ? 0 : 1], ownsHandle: true);
            _child = new SafePipeHandle(descriptors[parentReads ? 1 : 0], ownsHandle: true);
            _direction = parentReads ? PipeDirection.In : PipeDirection.Out;
            if (Native.fcntl(descriptors[0], 2, 1) < 0 || Native.fcntl(descriptors[1], 2, 1) < 0)
            {
                var error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error);
            }
        }

        internal void Redirect(IntPtr actions, int descriptor)
        {
            var child = (int)_child.DangerousGetHandle();
            var parent = (int)_parent!.DangerousGetHandle();
            Check(Native.posix_spawn_file_actions_adddup2(actions, child, descriptor));
            if (parent != descriptor) Check(Native.posix_spawn_file_actions_addclose(actions, parent));
            if (child != descriptor) Check(Native.posix_spawn_file_actions_addclose(actions, child));
        }

        internal Stream TakeParent()
        {
            var stream = new AnonymousPipeClientStream(_direction, _parent!);
            _parent = null;
            return stream;
        }

        public void Dispose() { _parent?.Dispose(); _child.Dispose(); }
    }

    private sealed class NativeStrings : IDisposable
    {
        private readonly IntPtr[] _strings;
        internal IntPtr Pointer { get; }
        internal NativeStrings(IEnumerable<string> values)
        {
            _strings = values.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            Pointer = Marshal.AllocHGlobal((_strings.Length + 1) * IntPtr.Size);
            Marshal.Copy(_strings, 0, Pointer, _strings.Length);
            Marshal.WriteIntPtr(Pointer, _strings.Length * IntPtr.Size, IntPtr.Zero);
        }

        public void Dispose()
        {
            foreach (var value in _strings) Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    private static class Native
    {
        private const string LibC = "libc";
        [DllImport(LibC, SetLastError = true)] internal static extern int pipe([Out] int[] descriptors);
        [DllImport(LibC, SetLastError = true)] internal static extern int fcntl(int descriptor, int command, int value);
        [DllImport(LibC, SetLastError = true)] internal static extern int kill(int pid, int signal);
        [DllImport(LibC, SetLastError = true)] internal static extern int waitpid(int pid, out int status, int options);
        [DllImport(LibC)] internal static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
        [DllImport(LibC)] internal static extern int posix_spawn_file_actions_init(IntPtr actions);
        [DllImport(LibC)] internal static extern int posix_spawn_file_actions_destroy(IntPtr actions);
        [DllImport(LibC)] internal static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int source, int target);
        [DllImport(LibC)] internal static extern int posix_spawn_file_actions_addclose(IntPtr actions, int descriptor);
        [DllImport(LibC)] internal static extern int posix_spawnattr_init(IntPtr attributes);
        [DllImport(LibC)] internal static extern int posix_spawnattr_destroy(IntPtr attributes);
        [DllImport(LibC)] internal static extern int posix_spawnattr_setpgroup(IntPtr attributes, int group);
        [DllImport(LibC)] internal static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);
        [DllImport(LibC)] internal static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(LibC)] internal static extern int posix_spawnp(out int pid,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string file, IntPtr actions, IntPtr attributes, IntPtr argv, IntPtr environment);
    }
}
