using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static Daqifi.Core.Communication.Transport.MacOsHidTransportDevice;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// macOS HID platform backed directly by the native IOKit/CoreFoundation system
/// frameworks. Used instead of <see cref="HidLibraryPlatform"/> on macOS, where
/// HidSharp 2.6.4 enumerates 0 HID devices (daqifi-core #262): its macOS backend
/// walks the IOKit registry and silently drops the PIC32 HID bootloader, so the
/// firmware-update flow stalls at <c>WaitingForBootloader</c>. IOKit enumerates
/// and drives the same device correctly, so this adapter slots in behind the
/// existing <see cref="IHidPlatform"/> seam with no public-API or dependency change.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsHidPlatform : IHidPlatform
{
    public IReadOnlyList<IHidTransportDevice> EnumerateDevices()
    {
        var manager = NativeMethods.IOHIDManagerCreate(IntPtr.Zero, NativeMethods.kIOHIDOptionsTypeNone);
        if (manager == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "HID backend is unavailable. IOHIDManagerCreate failed to initialize USB HID enumeration.");
        }

        try
        {
            // Match all HID devices. We deliberately never call IOHIDManagerOpen:
            // opening the manager (which matches everything, including keyboards)
            // is gated by the macOS Input-Monitoring TCC policy and returns
            // kIOReturnNotPermitted (0xE00002E2). Enumeration via
            // IOHIDManagerCopyDevices and per-device IOHIDDeviceOpen of a vendor
            // HID device do not require that permission, which is why the PIC32
            // bootloader can be driven without elevated privileges.
            NativeMethods.IOHIDManagerSetDeviceMatching(manager, IntPtr.Zero);

            var deviceSet = NativeMethods.IOHIDManagerCopyDevices(manager);
            if (deviceSet == IntPtr.Zero)
            {
                return Array.Empty<IHidTransportDevice>();
            }

            try
            {
                var count = NativeMethods.CFSetGetCount(deviceSet);
                if (count <= 0)
                {
                    return Array.Empty<IHidTransportDevice>();
                }

                var values = new IntPtr[count];
                NativeMethods.CFSetGetValues(deviceSet, values);

                var devices = new List<IHidTransportDevice>((int)count);
                foreach (var deviceRef in values)
                {
                    if (deviceRef == IntPtr.Zero)
                    {
                        continue;
                    }

                    var device = MacOsHidTransportDevice.TryCreate(deviceRef);
                    if (device != null)
                    {
                        devices.Add(device);
                    }
                }

                return devices;
            }
            finally
            {
                NativeMethods.CFRelease(deviceSet);
            }
        }
        finally
        {
            NativeMethods.CFRelease(manager);
        }
    }
}

/// <summary>
/// IOKit-backed <see cref="IHidTransportDevice"/>. Output reports are sent with
/// <c>IOHIDDeviceSetReport</c>; input reports are delivered to a callback serviced
/// on a dedicated CFRunLoop thread and surfaced through a blocking queue so the
/// synchronous <see cref="Read"/> contract is preserved. The PIC32 bootloader uses
/// 64-byte unnumbered reports (report ID 0); IOKit takes the report ID as a separate
/// argument and excludes it from the report buffer, so — unlike the HidSharp path —
/// no report-ID byte is prepended/stripped here.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsHidTransportDevice : IHidTransportDevice, IDisposable
{
    // The PIC32 HID bootloader uses 64-byte IN/OUT reports. Used as a fallback when
    // the device omits the max-report-size properties from its IORegistry entry.
    private const int DefaultReportLength = 64;

    // CFRunLoopRunInMode time slice. Input callbacks fire during the slice, so this
    // only bounds how quickly a missed CFRunLoopStop is observed on teardown.
    private const double RunLoopSliceSeconds = 0.2;

    /// <summary>
    /// Where this device is in its open/close life. Every native resource the session holds
    /// follows from it, so the one value answers whether a reopen or a release is safe.
    /// </summary>
    private enum HidSessionState
    {
        /// <summary>
        /// The device is not open and no input thread exists; only the retained device ref is
        /// held. The only state <see cref="Open"/> may start a session from.
        /// </summary>
        Closed,

        /// <summary>
        /// <see cref="Open"/> has opened the device and is waiting for the input thread to schedule
        /// its callback. Only ever seen under the lifecycle lock.
        /// </summary>
        Opening,

        /// <summary>The device is open and the input thread is servicing its run loop.</summary>
        Open,

        /// <summary>
        /// A close timed out waiting for the input thread to stop. The device, input buffer,
        /// callback and run loop stay allocated because the live thread may still use them, and
        /// that thread finishes the teardown itself when it exits (see
        /// <see cref="FinishAbandonedClose"/>). <see cref="Close"/> has nothing left to do and
        /// <see cref="Open"/> refuses.
        /// </summary>
        CloseLeaked,

        /// <summary>
        /// <see cref="Dispose"/> ran while the close was leaked. The exiting input thread
        /// finishes the teardown and also releases the device ref.
        /// </summary>
        ReleasePending,

        /// <summary>The device ref has been released. Terminal.</summary>
        Released,
    }

    // Who finishes the teardown when a close gives up waiting on the input thread. Exactly one
    // of the closer and the exiting thread wins the CompareExchange from None: if the thread
    // exits first the closer tears down as usual; if the closer abandons first the thread does.
    private const int ReaderHandoffNone = 0;
    private const int ReaderHandoffAbandoned = 1;
    private const int ReaderHandoffExited = 2;

    private readonly IntPtr _deviceRef;            // CFRetain'd in TryCreate; CFRelease'd on the move to Released (finalizer is the backstop).
    private readonly int _maxInputReportLength;
    private readonly int _maxOutputReportLength;
    private readonly object _lifecycleLock = new();

    private BlockingCollection<byte[]>? _inbox;
    private Thread? _ioThread;
    private ManualResetEventSlim? _readyEvent;
    private NativeMethods.IOHIDReportCallback? _inputCallback; // field keeps the delegate alive for native calls.
    private IntPtr _inputBuffer;
    private volatile IntPtr _runLoop;
    private volatile bool _stopRequested;          // The signal to the input thread, not lifecycle state.
    private volatile HidSessionState _state = HidSessionState.Closed; // Written only by TransitionTo.
    private int _readerHandoff;                    // ReaderHandoff*; reset by Open, claimed by Interlocked.

    private MacOsHidTransportDevice(
        IntPtr deviceRef,
        int vendorId,
        int productId,
        string devicePath,
        string? serialNumber,
        string? productName,
        int maxInputReportLength,
        int maxOutputReportLength)
    {
        _deviceRef = deviceRef;
        VendorId = vendorId;
        ProductId = productId;
        DevicePath = devicePath;
        SerialNumber = serialNumber;
        ProductName = productName;
        _maxInputReportLength = maxInputReportLength;
        _maxOutputReportLength = maxOutputReportLength;
    }

    ~MacOsHidTransportDevice()
    {
        // Backstop for the retained device ref when Dispose() was not called. The input
        // thread's frame keeps this object reachable while it runs, so by the time the
        // finalizer runs that thread has exited and nothing native still uses the ref.
        if (_state != HidSessionState.Released && _deviceRef != IntPtr.Zero)
        {
            NativeMethods.CFRelease(_deviceRef);
        }
    }

    /// <summary>
    /// Releases the retained IOKit device reference deterministically rather than
    /// at finalization. Callers (the enumerator and transport) dispose every
    /// enumerated device they do not keep; the bootloader-wait poll loop enumerates
    /// frequently, so prompt release avoids piling up finalizable objects and
    /// native handles. Closes the device first if it is still open. If that close is
    /// leaked (the input thread would not stop) the device ref is kept, because that
    /// thread still passes it to IOKit; the thread releases it when it exits.
    /// </summary>
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            CloseInternal();
            switch (_state)
            {
                case HidSessionState.CloseLeaked:
                    TransitionTo(HidSessionState.ReleasePending);
                    return;
                case HidSessionState.ReleasePending:
                    return;
                case HidSessionState.Closed:
                    ReleaseDeviceRef();
                    break;
            }
        }

        GC.SuppressFinalize(this);
    }

    public int VendorId { get; }
    public int ProductId { get; }
    public string DevicePath { get; }
    public string? SerialNumber { get; }
    public string? ProductName { get; }
    // Tracks the Open/Close lifecycle, not physical attachment. Unlike the
    // HidSharp backend (which probes stream validity), this does not flip to
    // false on its own when the device is unplugged mid-flow; removal surfaces
    // as a failed Write/Read (IOException/TimeoutException) which the firmware
    // retry layer already handles.
    public bool IsConnected => _state == HidSessionState.Open;

    /// <summary>
    /// Reads the addressing metadata for a device returned by enumeration and
    /// retains its IOKit reference so it remains valid after the enumeration set
    /// and manager are released. Returns null for entries without a readable
    /// VID/PID (devices we cannot address).
    /// </summary>
    internal static MacOsHidTransportDevice? TryCreate(IntPtr deviceRef)
    {
        var vendorId = GetNumberProperty(deviceRef, NativeMethods.kIOHIDVendorIDKey);
        var productId = GetNumberProperty(deviceRef, NativeMethods.kIOHIDProductIDKey);
        if (vendorId == null || productId == null)
        {
            return null;
        }

        var serialNumber = HidStringNormalization.Normalize(GetStringProperty(deviceRef, NativeMethods.kIOHIDSerialNumberKey));
        var productName = HidStringNormalization.Normalize(GetStringProperty(deviceRef, NativeMethods.kIOHIDProductKey));

        var maxInput = GetNumberProperty(deviceRef, NativeMethods.kIOHIDMaxInputReportSizeKey) ?? 0;
        var maxOutput = GetNumberProperty(deviceRef, NativeMethods.kIOHIDMaxOutputReportSizeKey) ?? 0;
        if (maxInput <= 0)
        {
            maxInput = DefaultReportLength;
        }

        if (maxOutput <= 0)
        {
            maxOutput = DefaultReportLength;
        }

        var devicePath = BuildDevicePath(deviceRef, vendorId.Value, productId.Value);
        if (devicePath == null)
        {
            // No stable identity to reconnect by; skip rather than hand out a
            // path that changes between enumeration passes (which the transport
            // sorts and serial-filters by).
            return null;
        }

        NativeMethods.CFRetain(deviceRef);
        return new MacOsHidTransportDevice(
            deviceRef,
            vendorId.Value,
            productId.Value,
            devicePath,
            serialNumber,
            productName,
            maxInput,
            maxOutput);
    }

    public void Open(bool exclusive)
    {
        lock (_lifecycleLock)
        {
            switch (_state)
            {
                case HidSessionState.Open:
                    return;
                case HidSessionState.Released:
                case HidSessionState.ReleasePending:
                    throw new ObjectDisposedException(nameof(MacOsHidTransportDevice));
                case HidSessionState.CloseLeaked:
                    // Reopening would reset the stop signal the old thread still polls and
                    // overwrite the run loop it unschedules from, so refuse until that thread
                    // has exited and finished the close.
                    throw new IOException(
                        "Cannot reopen the HID device: its previous input thread did not stop when it was closed. " +
                        "Retry after that thread exits, or reconnect to re-enumerate the device.");
            }

            // A2 (stray-write guard): when exclusive, seize the device so no other user-mode opener
            // can drive it while we hold it — the macOS equivalent of the Windows dwShareMode=0 open.
            // Best-effort, mirroring the HidSharp backend: a refused seize falls back to a shared
            // open so a flash that works today is not regressed.
            var options = exclusive
                ? NativeMethods.kIOHIDOptionsTypeSeizeDevice
                : NativeMethods.kIOHIDOptionsTypeNone;
            var seizeResult = NativeMethods.kIOReturnSuccess;
            var result = NativeMethods.IOHIDDeviceOpen(_deviceRef, options);
            if (exclusive)
            {
                seizeResult = result;
                if (result != NativeMethods.kIOReturnSuccess)
                {
                    // Refused seize falls back to a shared open so a flash that works today is not regressed.
                    result = NativeMethods.IOHIDDeviceOpen(_deviceRef, NativeMethods.kIOHIDOptionsTypeNone);
                }
            }

            if (result != NativeMethods.kIOReturnSuccess)
            {
                // On a double failure surface both codes so the seize and the shared fallback are distinguishable.
                throw new IOException(
                    exclusive
                        ? $"IOHIDDeviceOpen failed (seize IOReturn=0x{seizeResult:X8}, shared IOReturn=0x{result:X8})."
                        : $"IOHIDDeviceOpen failed (IOReturn=0x{result:X8}).");
            }

            TransitionTo(HidSessionState.Opening);
            _stopRequested = false;
            _readerHandoff = ReaderHandoffNone;
            _inbox = new BlockingCollection<byte[]>();
            _inputBuffer = Marshal.AllocHGlobal(_maxInputReportLength);
            _inputCallback = OnInputReport;
            _readyEvent = new ManualResetEventSlim(false);

            _ioThread = new Thread(RunInputLoop)
            {
                IsBackground = true,
                Name = "macos-hid-input"
            };
            _ioThread.Start();

            // Block until the input callback is scheduled on the run loop so a
            // Write/Read issued immediately after Open cannot miss early reports.
            if (!_readyEvent.Wait(TimeSpan.FromSeconds(5)))
            {
                CloseInternal();
                throw new IOException("Timed out starting the macOS HID input run loop.");
            }

            TransitionTo(HidSessionState.Open);
        }
    }

    public void Close()
    {
        lock (_lifecycleLock)
        {
            CloseInternal();
        }
    }

    public bool Write(byte[] data, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (_state != HidSessionState.Open)
        {
            throw new InvalidOperationException("HID device is not open.");
        }

        if (data.Length > _maxOutputReportLength)
        {
            throw new IOException(
                $"HID payload length {data.Length} exceeds max report size {_maxOutputReportLength}.");
        }

        // Send a full-length report (zero-padded), matching the fixed 64-byte OUT
        // report the bootloader expects. IOKit takes the report ID separately, so
        // the buffer carries protocol payload only — no leading report-ID byte.
        var report = new byte[_maxOutputReportLength];
        Array.Copy(data, report, data.Length);

        // IOHIDDeviceSetReport is synchronous and can stall on an unresponsive
        // device. Run it on a worker and bound the wait by timeoutMs so the
        // transport's WriteTimeout is honored, mirroring the HidSharp backend's
        // stream.WriteTimeout. The captured report keeps the marshalled buffer
        // pinned until the native call returns even if we stop waiting.
        var setReport = Task.Run(() => NativeMethods.IOHIDDeviceSetReport(
            _deviceRef,
            NativeMethods.kIOHIDReportTypeOutput,
            reportID: 0,
            report,
            report.Length));

        if (!setReport.Wait(timeoutMs))
        {
            throw new IOException($"IOHIDDeviceSetReport timed out after {timeoutMs} ms.");
        }

        if (setReport.Result != NativeMethods.kIOReturnSuccess)
        {
            // Surface the IOReturn code: it is the most useful datum for
            // diagnosing an on-device stall (e.g. 0xE00002C0 not-attached on
            // removal), and the firmware retry layer treats IOException the same
            // as the HidSharp path's coarse write-failure.
            throw new IOException($"IOHIDDeviceSetReport failed (IOReturn=0x{setReport.Result:X8}).");
        }

        return true;
    }

    public Task<bool> WriteAsync(byte[] data, int timeoutMs)
    {
        return Task.FromResult(Write(data, timeoutMs));
    }

    public HidTransportReadResult Read(int timeoutMs)
    {
        if (_state != HidSessionState.Open)
        {
            throw new InvalidOperationException("HID device is not open.");
        }

        var inbox = _inbox;
        if (inbox == null)
        {
            return HidTransportReadResult.Error(Array.Empty<byte>(), "HID device input queue is unavailable.");
        }

        try
        {
            if (inbox.TryTake(out var report, timeoutMs))
            {
                return HidTransportReadResult.Success(report);
            }

            return HidTransportReadResult.TimedOut(Array.Empty<byte>());
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return HidTransportReadResult.Error(Array.Empty<byte>(), "HID device was closed during read.");
        }
    }

    public Task<HidTransportReadResult> ReadAsync(int timeoutMs)
    {
        return Task.FromResult(Read(timeoutMs));
    }

    private void RunInputLoop()
    {
        try
        {
            _runLoop = NativeMethods.CFRunLoopGetCurrent();

            NativeMethods.IOHIDDeviceRegisterInputReportCallback(
                _deviceRef,
                _inputBuffer,
                _maxInputReportLength,
                _inputCallback!,
                IntPtr.Zero);

            NativeMethods.IOHIDDeviceScheduleWithRunLoop(
                _deviceRef,
                _runLoop,
                NativeMethods.RunLoopDefaultMode);

            _readyEvent!.Set();

            // Slice the run loop so a missed CFRunLoopStop (if Close races the
            // start) is still observed via _stopRequested. Input callbacks fire
            // during each slice, so this does not add read latency.
            while (!_stopRequested)
            {
                NativeMethods.CFRunLoopRunInMode(
                    NativeMethods.RunLoopDefaultMode,
                    RunLoopSliceSeconds,
                    returnAfterSourceHandled: false);
            }
        }
        catch
        {
            // Never let an exception escape the dedicated thread; the finally
            // still revokes the registration and releases Open()'s wait.
        }
        finally
        {
            // Revoke the input registration and unschedule BEFORE the thread
            // exits, so IOKit has dropped the input-buffer pointer and callback
            // thunk before CloseInternal frees them — regardless of how the loop
            // terminated. Without this, an exception escaping the loop would skip
            // teardown and leave IOKit holding a soon-to-be-freed buffer.
            if (_runLoop != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.IOHIDDeviceRegisterInputReportCallback(
                        _deviceRef, IntPtr.Zero, 0, null, IntPtr.Zero);
                }
                catch
                {
                    // Best effort: nothing actionable if revocation itself fails.
                }

                try
                {
                    NativeMethods.IOHIDDeviceUnscheduleFromRunLoop(
                        _deviceRef, _runLoop, NativeMethods.RunLoopDefaultMode);
                }
                catch
                {
                    // Best effort.
                }
            }

            _readyEvent?.Set();

            // Last thing the thread does: claim the hand-off. If a close already gave up
            // waiting on this thread, nobody else will free what it left allocated.
            if (Interlocked.CompareExchange(ref _readerHandoff, ReaderHandoffExited, ReaderHandoffNone)
                == ReaderHandoffAbandoned)
            {
                FinishAbandonedClose();
            }
        }
    }

    /// <summary>
    /// Runs on the exiting input thread after a close gave up waiting on it. IOKit has
    /// already dropped the callback and buffer (the caller's finally revoked them), so
    /// the session's resources can be freed now. Never blocks for long: the closer that
    /// abandoned the thread only holds the lock until it records <see cref="HidSessionState.CloseLeaked"/>,
    /// and Close/Dispose/Open in that state return without waiting.
    /// </summary>
    private void FinishAbandonedClose()
    {
        try
        {
            lock (_lifecycleLock)
            {
                var disposeRequested = _state == HidSessionState.ReleasePending;
                ReleaseSessionResources();
                TransitionTo(HidSessionState.Closed);
                if (disposeRequested)
                {
                    ReleaseDeviceRef();
                    GC.SuppressFinalize(this);
                }
            }
        }
        catch
        {
            // Never let an exception escape the dedicated thread.
        }
    }

    private void OnInputReport(
        IntPtr context,
        int result,
        IntPtr sender,
        int type,
        uint reportId,
        IntPtr report,
        nint reportLength)
    {
        if (result != NativeMethods.kIOReturnSuccess || report == IntPtr.Zero)
        {
            return;
        }

        var length = (int)reportLength;
        if (length <= 0)
        {
            return;
        }

        var managed = new byte[length];
        Marshal.Copy(report, managed, 0, length);

        try
        {
            _inbox?.Add(managed);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The inbox was completed/disposed during Close(); drop the report.
        }
    }

    private void CloseInternal()
    {
        // In CloseLeaked/ReleasePending the input thread owns the rest of the teardown.
        if (_state is not (HidSessionState.Opening or HidSessionState.Open))
        {
            return;
        }

        _stopRequested = true;

        if (_runLoop != IntPtr.Zero)
        {
            NativeMethods.CFRunLoopStop(_runLoop);
        }

        // The run-loop thread deregisters the input callback and unschedules the
        // device in its finally before exiting, so once it has actually stopped no
        // callback can touch the native buffer. Verify it stopped before freeing
        // anything — if a wedged native run loop fails to stop in time, leave the
        // device/buffer/callback allocated rather than free memory IOKit might still
        // write into, which would crash the process. The thread frees them itself when
        // it exits (FinishAbandonedClose).
        var thread = _ioThread;
        var threadStopped = thread is null || !thread.IsAlive || thread.Join(TimeSpan.FromSeconds(5));

        _inbox?.CompleteAdding();

        // A thread that finished its finally between the join timing out and here has
        // already claimed Exited, so the CompareExchange fails and we tear down as usual.
        if (!threadStopped &&
            Interlocked.CompareExchange(ref _readerHandoff, ReaderHandoffAbandoned, ReaderHandoffNone)
                == ReaderHandoffNone)
        {
            TransitionTo(HidSessionState.CloseLeaked);
            return;
        }

        ReleaseSessionResources();
        TransitionTo(HidSessionState.Closed);
    }

    /// <summary>
    /// Frees what an open session holds. Called exactly once per session, under the
    /// lifecycle lock, by whichever of the closer and the exiting input thread owns the
    /// teardown, and only once that thread can no longer touch any of it.
    /// </summary>
    private void ReleaseSessionResources()
    {
        _ioThread = null;
        _runLoop = IntPtr.Zero;

        NativeMethods.IOHIDDeviceClose(_deviceRef, NativeMethods.kIOHIDOptionsTypeNone);

        if (_inputBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_inputBuffer);
            _inputBuffer = IntPtr.Zero;
        }

        _inputCallback = null;

        var inbox = _inbox;
        _inbox = null;
        inbox?.Dispose();

        _readyEvent?.Dispose();
        _readyEvent = null;
    }

    /// <summary>Releases the retained device ref. Only valid from <see cref="HidSessionState.Closed"/>.</summary>
    private void ReleaseDeviceRef()
    {
        TransitionTo(HidSessionState.Released);
        if (_deviceRef != IntPtr.Zero)
        {
            NativeMethods.CFRelease(_deviceRef);
        }
    }

    /// <summary>
    /// The only writer of <see cref="_state"/>. Callers hold the lifecycle lock. A move not
    /// listed here is a bug in this class, so it throws rather than being ignored.
    /// </summary>
    private void TransitionTo(HidSessionState next)
    {
        var allowed = (_state, next) switch
        {
            (HidSessionState.Closed, HidSessionState.Opening) => true,
            (HidSessionState.Closed, HidSessionState.Released) => true,
            (HidSessionState.Opening, HidSessionState.Open) => true,
            (HidSessionState.Opening, HidSessionState.Closed) => true,
            (HidSessionState.Opening, HidSessionState.CloseLeaked) => true,
            (HidSessionState.Open, HidSessionState.Closed) => true,
            (HidSessionState.Open, HidSessionState.CloseLeaked) => true,
            (HidSessionState.CloseLeaked, HidSessionState.ReleasePending) => true,
            // Only FinishAbandonedClose makes these two, on the exiting input thread.
            (HidSessionState.CloseLeaked, HidSessionState.Closed) => true,
            (HidSessionState.ReleasePending, HidSessionState.Closed) => true,
            _ => false,
        };

        if (!allowed)
        {
            throw new InvalidOperationException($"Invalid HID session transition {_state} -> {next}.");
        }

        _state = next;
    }

    private static string? BuildDevicePath(IntPtr deviceRef, int vendorId, int productId)
    {
        // A stable, unique, non-empty path is required (HidDeviceInfo rejects empty
        // paths, and the transport filters/sorts by path). The IORegistry entry ID
        // is globally unique per service; fall back to LocationID. Returns null if
        // neither is available — a per-CopyDevices ref pointer is not stable across
        // enumeration passes, so a device with no durable identity is skipped.
        string suffix;
        var service = NativeMethods.IOHIDDeviceGetService(deviceRef);
        if (service != 0 &&
            NativeMethods.IORegistryEntryGetRegistryEntryID(service, out var entryId) == NativeMethods.kIOReturnSuccess)
        {
            suffix = $"ID_{entryId:X}";
        }
        else
        {
            var locationId = GetNumberProperty(deviceRef, NativeMethods.kIOHIDLocationIDKey);
            if (!locationId.HasValue)
            {
                return null;
            }

            suffix = $"LOC_{(uint)locationId.Value:X8}";
        }

        return $"IOKit:VID_{vendorId:X4}&PID_{productId:X4}&{suffix}";
    }

    private static int? GetNumberProperty(IntPtr deviceRef, string key)
    {
        var cfKey = NativeMethods.CFStringCreateWithCString(
            IntPtr.Zero, key, NativeMethods.kCFStringEncodingUTF8);
        if (cfKey == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // IOHIDDeviceGetProperty follows the CoreFoundation "Get" rule: the
            // returned value is not owned, so it must not be released here.
            var value = NativeMethods.IOHIDDeviceGetProperty(deviceRef, cfKey);
            if (value == IntPtr.Zero)
            {
                return null;
            }

            return NativeMethods.CFNumberGetValue(value, NativeMethods.kCFNumberSInt32Type, out var result)
                ? result
                : null;
        }
        finally
        {
            NativeMethods.CFRelease(cfKey);
        }
    }

    private static string? GetStringProperty(IntPtr deviceRef, string key)
    {
        var cfKey = NativeMethods.CFStringCreateWithCString(
            IntPtr.Zero, key, NativeMethods.kCFStringEncodingUTF8);
        if (cfKey == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var value = NativeMethods.IOHIDDeviceGetProperty(deviceRef, cfKey);
            if (value == IntPtr.Zero)
            {
                return null;
            }

            var buffer = new byte[512];
            if (!NativeMethods.CFStringGetCString(value, buffer, buffer.Length, NativeMethods.kCFStringEncodingUTF8))
            {
                return null;
            }

            var nullIndex = Array.IndexOf(buffer, (byte)0);
            var byteCount = nullIndex < 0 ? buffer.Length : nullIndex;
            return Encoding.UTF8.GetString(buffer, 0, byteCount);
        }
        finally
        {
            NativeMethods.CFRelease(cfKey);
        }
    }

    [SupportedOSPlatform("macos")]
    internal static class NativeMethods
    {
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string LibSystem = "/usr/lib/libSystem.dylib";

        internal const uint kIOHIDOptionsTypeNone = 0;
        internal const uint kIOHIDOptionsTypeSeizeDevice = 1;
        internal const int kIOReturnSuccess = 0;
        internal const int kIOHIDReportTypeOutput = 1;
        internal const int kCFNumberSInt32Type = 3;
        internal const uint kCFStringEncodingUTF8 = 0x08000100;

        internal const string kIOHIDVendorIDKey = "VendorID";
        internal const string kIOHIDProductIDKey = "ProductID";
        internal const string kIOHIDSerialNumberKey = "SerialNumber";
        internal const string kIOHIDProductKey = "Product";
        internal const string kIOHIDMaxInputReportSizeKey = "MaxInputReportSize";
        internal const string kIOHIDMaxOutputReportSizeKey = "MaxOutputReportSize";
        internal const string kIOHIDLocationIDKey = "LocationID";

        // CFIndex reportLength is native-word-sized; the callback is invoked by
        // IOKit with the C calling convention.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void IOHIDReportCallback(
            IntPtr context,
            int result,
            IntPtr sender,
            int type,
            uint reportId,
            IntPtr report,
            nint reportLength);

        internal static readonly IntPtr RunLoopDefaultMode = ResolveRunLoopDefaultMode();

        [DllImport(IOKit)]
        internal static extern IntPtr IOHIDManagerCreate(IntPtr allocator, uint options);

        [DllImport(IOKit)]
        internal static extern void IOHIDManagerSetDeviceMatching(IntPtr manager, IntPtr matching);

        [DllImport(IOKit)]
        internal static extern IntPtr IOHIDManagerCopyDevices(IntPtr manager);

        [DllImport(IOKit)]
        internal static extern int IOHIDDeviceOpen(IntPtr device, uint options);

        [DllImport(IOKit)]
        internal static extern int IOHIDDeviceClose(IntPtr device, uint options);

        [DllImport(IOKit)]
        internal static extern IntPtr IOHIDDeviceGetProperty(IntPtr device, IntPtr key);

        [DllImport(IOKit)]
        internal static extern int IOHIDDeviceSetReport(
            IntPtr device, int reportType, nint reportID, byte[] report, nint reportLength);

        [DllImport(IOKit)]
        internal static extern void IOHIDDeviceRegisterInputReportCallback(
            IntPtr device, IntPtr report, nint reportLength, IOHIDReportCallback? callback, IntPtr context);

        [DllImport(IOKit)]
        internal static extern void IOHIDDeviceScheduleWithRunLoop(IntPtr device, IntPtr runLoop, IntPtr runLoopMode);

        [DllImport(IOKit)]
        internal static extern void IOHIDDeviceUnscheduleFromRunLoop(IntPtr device, IntPtr runLoop, IntPtr runLoopMode);

        [DllImport(IOKit)]
        internal static extern uint IOHIDDeviceGetService(IntPtr device);

        [DllImport(IOKit)]
        internal static extern int IORegistryEntryGetRegistryEntryID(uint entry, out ulong entryId);

        [DllImport(CoreFoundation)]
        internal static extern void CFRelease(IntPtr cf);

        [DllImport(CoreFoundation)]
        internal static extern IntPtr CFRetain(IntPtr cf);

        [DllImport(CoreFoundation)]
        internal static extern nint CFSetGetCount(IntPtr theSet);

        [DllImport(CoreFoundation)]
        internal static extern void CFSetGetValues(IntPtr theSet, [Out] IntPtr[] values);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool CFNumberGetValue(IntPtr number, nint theType, out int value);

        [DllImport(CoreFoundation)]
        internal static extern IntPtr CFStringCreateWithCString(
            IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool CFStringGetCString(
            IntPtr theString, [Out] byte[] buffer, nint bufferSize, uint encoding);

        [DllImport(CoreFoundation)]
        internal static extern IntPtr CFRunLoopGetCurrent();

        [DllImport(CoreFoundation)]
        internal static extern int CFRunLoopRunInMode(
            IntPtr mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

        [DllImport(CoreFoundation)]
        internal static extern void CFRunLoopStop(IntPtr runLoop);

        [DllImport(LibSystem)]
        private static extern IntPtr dlopen(string path, int mode);

        [DllImport(LibSystem)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        private static IntPtr ResolveRunLoopDefaultMode()
        {
            // kCFRunLoopDefaultMode is a CFStringRef data symbol, which cannot be
            // bound via DllImport. Resolve it through dlsym; fall back to a CFString
            // with the same value (run-loop modes are matched by string equality).
            const int RTLD_LAZY = 1;
            var handle = dlopen(CoreFoundation, RTLD_LAZY);
            if (handle != IntPtr.Zero)
            {
                var symbol = dlsym(handle, "kCFRunLoopDefaultMode");
                if (symbol != IntPtr.Zero)
                {
                    var mode = Marshal.ReadIntPtr(symbol);
                    if (mode != IntPtr.Zero)
                    {
                        return mode;
                    }
                }
            }

            return CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopDefaultMode", kCFStringEncodingUTF8);
        }
    }
}
