using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Daqifi.Core.Firmware.Internal;

/// <summary>
/// Owns a Windows process family, including workers whose launcher has exited.
/// The job is assigned atomically by CreateProcess, before the launcher can start a child.
/// Process.Kill(true) cannot rediscover children after their parent has exited.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobProcessHandle : IProcessHandle
{
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _process;
    private readonly ProcessWaitHandle _waitHandle;
    private readonly RegisteredWaitHandle _registration;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WindowsJobProcessHandle(
        SafeFileHandle job, SafeFileHandle process,
        SafeFileHandle output, SafeFileHandle error, SafeFileHandle input, bool redirectInput)
    {
        _job = job;
        _process = process;
        StandardOutput = new StreamReader(new FileStream(output, FileAccess.Read), Console.OutputEncoding);
        StandardError = new StreamReader(new FileStream(error, FileAccess.Read), Console.OutputEncoding);
        if (redirectInput)
        {
            StandardInput = new StreamWriter(new FileStream(input, FileAccess.Write), Console.InputEncoding);
        }
        else
        {
            input.Dispose();
            StandardInput = TextWriter.Null;
        }

        _waitHandle = new ProcessWaitHandle(process.DangerousGetHandle());
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _waitHandle, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            _exited, Timeout.Infinite, executeOnlyOnce: true);
    }

    public bool HasExited => _waitHandle.WaitOne(0);

    public int ExitCode
    {
        get
        {
            if (!HasExited)
            {
                throw new InvalidOperationException("The process has not exited.");
            }

            Check(Native.GetExitCodeProcess(_process, out var code));
            return unchecked((int)code);
        }
    }

    public TextReader StandardOutput { get; }
    public TextReader StandardError { get; }
    public TextWriter StandardInput { get; }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

    public void KillProcessTree() => Check(Native.TerminateJobObject(_job, 1));

    public void Dispose()
    {
        // The private job belongs to this one invocation. Close any surviving worker before
        // closing its pipes, even if the launcher's process handle is already signaled.
        _ = Native.TerminateJobObject(_job, 1);
        _registration.Unregister(null);
        _waitHandle.Dispose();
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _process.Dispose();
        _job.Dispose();
    }

    internal static IProcessHandle Start(ExternalProcessRequest request)
    {
        using var output = new RedirectedPipe(parentReads: true);
        using var error = new RedirectedPipe(parentReads: true);
        using var input = new RedirectedPipe(parentReads: false);
        var outputChild = output.Child;
        var errorChild = error.Child;
        var inputChild = input.Child;
        var job = new SafeFileHandle(Native.CreateJobObjectW(IntPtr.Zero, null), ownsHandle: true);
        SafeFileHandle? process = null;
        var attributes = IntPtr.Zero;
        var jobList = IntPtr.Zero;
        var inheritedHandles = IntPtr.Zero;
        var initialized = false;
        try
        {
            if (job.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            nuint size = 0;
            _ = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((nint)size));
            Check(Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
            initialized = true;
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            // PROC_THREAD_ATTRIBUTE_JOB_LIST: workers inherit this job at creation, so there is
            // no interval in which a quick launcher can exit before assignment.
            Check(Native.UpdateProcThreadAttribute(attributes, 0, 0x0002000D,
                jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));

            inheritedHandles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(inheritedHandles, 0, inputChild.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, outputChild.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, 2 * IntPtr.Size, errorChild.DangerousGetHandle());
            Check(Native.UpdateProcThreadAttribute(attributes, 0, 0x00020002,
                inheritedHandles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x00000100, // STARTF_USESTDHANDLES
                    StandardInput = inputChild.DangerousGetHandle(),
                    StandardOutput = outputChild.DangerousGetHandle(),
                    StandardError = errorChild.DangerousGetHandle(),
                },
                AttributeList = attributes,
            };
            // FileName is an executable path, never a shell fragment. Windows paths cannot
            // contain quotes; reject one instead of changing the program being launched.
            if (request.FileName.Contains('"', StringComparison.Ordinal))
            {
                throw new ArgumentException("Executable paths cannot contain a double quote.", nameof(request));
            }

            var command = new StringBuilder($"\"{request.FileName}\" {request.Arguments ?? string.Empty}");
            Check(Native.CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, true,
                0x08080000, IntPtr.Zero,
                string.IsNullOrWhiteSpace(request.WorkingDirectory) ? null : request.WorkingDirectory,
                ref startup, out var information)); // CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT
            process = new SafeFileHandle(information.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(information.Thread, ownsHandle: true);
            return new WindowsJobProcessHandle(job, process, output.TakeParent(), error.TakeParent(), input.TakeParent(),
                request.StandardInputResponseFactory != null);
        }
        catch
        {
            if (!job.IsInvalid)
            {
                _ = Native.TerminateJobObject(job, 1);
            }

            process?.Dispose();
            job.Dispose();
            throw;
        }
        finally
        {
            if (initialized)
            {
                Native.DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(jobList);
            Marshal.FreeHGlobal(inheritedHandles);
        }
    }

    private sealed class RedirectedPipe : IDisposable
    {
        private SafeFileHandle? _parent;
        internal SafeFileHandle Child { get; }

        internal RedirectedPipe(bool parentReads)
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            Check(Native.CreatePipe(out var read, out var write, ref security, 0));
            _parent = new SafeFileHandle(parentReads ? read : write, ownsHandle: true);
            Child = new SafeFileHandle(parentReads ? write : read, ownsHandle: true);
            try
            {
                Check(Native.SetHandleInformation(_parent, 1, 0));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal SafeFileHandle TakeParent()
        {
            var handle = _parent!;
            _parent = null;
            return handle;
        }

        public void Dispose()
        {
            _parent?.Dispose();
            Child.Dispose();
        }
    }

    private static void Check(bool success)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    private static class Native
    {
        private const string Kernel32 = "kernel32.dll";
        [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateJobObjectW(IntPtr security, string? name);
        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes security, int size);
        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute,
            IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
        [DllImport(Kernel32)]
        internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string? application, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
            IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
        [DllImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    }
}
