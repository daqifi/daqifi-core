using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Protocol;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Discovers DAQiFi devices connected via USB/Serial ports.
/// Probes each port by sending SCPI commands and validating protobuf responses.
/// </summary>
/// <remarks>
/// <para>
/// Port probes are claimed process-wide so a wedged USB CDC device can block at
/// most one thread per port (see issues #294 / #295). A pass that ends by timeout
/// or cancellation abandons its in-flight probe and leaves the claim behind while
/// that probe unwinds — bench-measured at ~490ms on a real Nq1.
/// </para>
/// <para>
/// A pass that starts inside that drain window waits (bounded by both the window
/// and the caller's own timeout / token) for the claim to clear before probing, so
/// a prompt retry after a timed-out pass finds the device instead of silently
/// reporting none. Callers therefore do not need to hand-roll a backoff, but they
/// should still allow a realistic timeout: a retry given less budget than the drain
/// needs will report nothing, exactly as it would have before.
/// </para>
/// </remarks>
public class SerialDeviceFinder : DeviceFinderBase
{
    #region Constants

    private const int DefaultBaudRate = 9600;
    private const int ProbeTimeoutMs = 1000;
    // Tightened for the GetDeviceInfo-only probe (closes #157):
    //   - 200ms wake instead of 1s — USB CDC enumerates fast
    //   - 1s response timeout instead of 4s — DAQiFi devices reply within
    //     a few hundred ms on USB
    //   - 300ms retry interval instead of 1s
    //   - 2 attempts instead of 3 — combined with VID/PID prefilter we no
    //     longer need to be defensive against probing other vendors' ports
    private const int DeviceWakeUpDelayMs = 200;
    private const int ResponseTimeoutMs = 1000;
    private const int MaxRetries = 2;
    private const int RetryIntervalMs = 300;
    private const int PollIntervalMs = 50;
    // Upper bound on concurrent SerialPort opens during a single discovery
    // pass. Most OS serial stacks tolerate 4-8 simultaneous opens cleanly;
    // beyond that, IO failures and slow opens stack up. Common case (with
    // VID/PID classifier) leaves 0-1 candidates so this cap rarely engages.
    //
    // Internal so the slot-contention test can size its port list from the cap
    // instead of hard-coding it — a raised cap would otherwise leave that test
    // silently under-subscribed and no longer testing contention at all.
    internal const int MaxParallelProbes = 4;
    // Hard ceiling on a single port probe. A wedged USB CDC device can hang
    // SerialPort.Open() inside native GetCommState indefinitely — no exception,
    // no cancellation (Open is uncancellable blocking I/O) — observed live on a
    // hung-firmware Nq1 (#294). The healthy probe worst case is ~1.5s
    // (DeviceWakeUpDelayMs 200 + EchoDisableSettleMs 250 + ResponseTimeoutMs 1000)
    // plus Open() itself; 3s gives ~2x headroom for a slow open (fresh
    // enumeration, parallel opens) while abandoning a genuinely stuck port fast
    // enough that discovery feels responsive (bench QA feedback 2026-07-13:
    // 8s felt sluggish next to the 2-3s sweep cadence).
    private const int DefaultPortProbeHardTimeoutMs = 3000;

    #endregion

    #region Private Fields

    private readonly int _baudRate;
    private readonly IUsbPortDescriptorProvider _usbPortDescriptorProvider;
    private readonly IUsbLocationProvider _usbLocationProvider;
    private readonly Func<string[]>? _portNameProvider;
    private readonly Func<string, CancellationToken, Task<IDeviceInfo?>>? _probeOverride;
    // Process-wide per-port probe claims. A port is CLAIMED (atomically, via
    // GetOrAdd) before its probe starts and released when the probe completes,
    // so at most one probe can ever be in flight or abandoned per port across
    // ALL finder instances — the desktop apps sweep every 2-3s and the MCP
    // DaqifiAgent constructs a fresh finder per call, so without this a port
    // whose SerialPort.Open() wedges in uncancellable native I/O would leak one
    // blocked thread-pool thread per sweep/call (Qodo PR #295 review #1;
    // step-3.5 audit x2: per-instance state lost the bound for per-call finders,
    // and a non-atomic check-then-act lost it for concurrent instances).
    //
    // Lifecycle: claim -> probe -> (completes: released by owner) or (times out:
    // claim marked abandoned; a continuation releases it the moment the stuck
    // I/O finally completes — device replug usually errors the stale handle —
    // and QuarantineRetryTtlMs bounds the worst case by letting ONE fresh
    // attempt replace a still-stuck claim per TTL window, so a recovered port
    // re-appears without a process restart at a bounded leak rate).
    //
    // A claim abandoned by a timed-out / cancelled pass is usually NOT wedged; it
    // drains in ~490ms. Within AbandonedClaimDrainWaitMs a fresh pass waits for it
    // rather than reporting the port absent — see ProbeSafelyAsync.
    //
    // STATIC on purpose: a wedged COM port is machine-global state, not finder
    // state.
    private static readonly ConcurrentDictionary<string, PortProbeClaim> PortClaims = new();

    private sealed class PortProbeClaim
    {
        /// <summary>Set right after Task.Run; null means claimed-but-starting.</summary>
        public volatile Task<IDeviceInfo?>? Probe;

        // -1 = in flight; >= 0 = abandoned at this Environment.TickCount64.
        private long _abandonedAtTicks = -1;
        public long AbandonedAtTicks
        {
            get => Interlocked.Read(ref _abandonedAtTicks);
            set => Interlocked.Exchange(ref _abandonedAtTicks, value);
        }
    }

    private static void ReleaseClaimIfOwned(string portName, PortProbeClaim claim)
    {
        // Atomic remove-only-if-same-claim: a TTL retry may have replaced the
        // entry with a newer claim that must survive this release.
        ((ICollection<KeyValuePair<string, PortProbeClaim>>)PortClaims)
            .Remove(new KeyValuePair<string, PortProbeClaim>(portName, claim));
    }

    // One fresh probe per wedged port per this window (monotonic ms).
    private const int DefaultQuarantineRetryTtlMs = 30_000;

    // Internal so tests can exercise the TTL retry without waiting 30s.
    internal static int QuarantineRetryTtlMs { get; set; } = DefaultQuarantineRetryTtlMs;

    // How long AFTER abandonment a claim is still treated as "probably draining"
    // rather than "wedged". A pass that ends by timeout or caller cancellation
    // abandons its in-flight probe, but that probe is usually NOT wedged — it is
    // unwinding TryGetDeviceInfoAsync's cleanup (consumer/producer StopSafely, DTR
    // drop + 50ms settle, port close). Bench-measured on a real Nq1 (fw 3.7.2, USB,
    // macOS): the claim was held 488/490/491ms across three runs, then auto-released
    // by the abandonment continuation.
    //
    // Within this window a fresh pass WAITS for the claim to clear instead of
    // skipping the port; past it, the port is presumed genuinely wedged and skipped
    // immediately as before. 1s is ~2x the measured drain — enough headroom for a
    // loaded thread pool, still short enough that a truly wedged port costs at most
    // one sub-second wait before ageing out of the window entirely.
    private const int DefaultAbandonedClaimDrainWaitMs = 1000;

    // Internal so tests can shrink the drain wait instead of burning ~1s per case.
    internal int AbandonedClaimDrainWaitMs { get; set; } = DefaultAbandonedClaimDrainWaitMs;

    /// <summary>
    /// Test seam: clears the process-wide port quarantine so hang-simulation
    /// tests can't contaminate each other through the shared dictionary.
    /// </summary>
    internal static void ResetPortQuarantineForTests()
    {
        PortClaims.Clear();
        QuarantineRetryTtlMs = DefaultQuarantineRetryTtlMs;
    }

    // Internal so tests can shrink the hard timeout instead of waiting 8s per case.
    internal int PortProbeHardTimeoutMs { get; set; } = DefaultPortProbeHardTimeoutMs;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the SerialDeviceFinder class with default baud rate.
    /// </summary>
    public SerialDeviceFinder() : this(DefaultBaudRate, usbPortDescriptorProvider: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the SerialDeviceFinder class.
    /// </summary>
    /// <param name="baudRate">The baud rate to use for serial connections.</param>
    public SerialDeviceFinder(int baudRate) : this(baudRate, usbPortDescriptorProvider: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the SerialDeviceFinder class with an
    /// explicit USB descriptor provider — primarily for tests that mock the
    /// platform-specific WMI / sysfs lookup.
    /// </summary>
    /// <param name="baudRate">The baud rate to use for serial connections.</param>
    /// <param name="usbPortDescriptorProvider">
    /// Provider used to resolve a port's USB Vendor / Product ID before
    /// opening it. When null, a platform-default provider is used (Windows
    /// → WMI, Linux → sysfs, others → no-op fallback). Pass
    /// <see cref="NullUsbPortDescriptorProvider.Instance"/> explicitly to
    /// force the legacy probe-everything behavior.
    /// </param>
    /// <param name="portNameProvider">
    /// Test seam: when non-null, supplies the candidate port-name list in
    /// place of <see cref="SerialStreamTransport.GetAvailablePortNames"/>.
    /// Lets unit tests deterministically exercise the descriptor / probe
    /// path on hosts (CI containers) that have no real serial ports.
    /// </param>
    /// <param name="usbLocationProvider">
    /// Provider used to resolve a discovered device's USB physical-location
    /// key. When null, a platform-default provider is used (Windows → WMI,
    /// others → no-op fallback).
    /// </param>
    /// <param name="probeOverride">
    /// Test seam: when non-null, replaces <see cref="TryGetDeviceInfoAsync"/>
    /// as the per-port probe. Lets unit tests simulate hung, slow, failing,
    /// or succeeding ports without real serial hardware (#294).
    /// </param>
    internal SerialDeviceFinder(
        int baudRate,
        IUsbPortDescriptorProvider? usbPortDescriptorProvider,
        Func<string[]>? portNameProvider = null,
        IUsbLocationProvider? usbLocationProvider = null,
        Func<string, CancellationToken, Task<IDeviceInfo?>>? probeOverride = null)
    {
        _baudRate = baudRate;
        _usbPortDescriptorProvider = usbPortDescriptorProvider
            ?? UsbPortDescriptorProviderFactory.CreateForCurrentPlatform();
        _portNameProvider = portNameProvider;
        _usbLocationProvider = usbLocationProvider
            ?? UsbLocationProviderFactory.CreateForCurrentPlatform();
        _probeOverride = probeOverride;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Discovers devices asynchronously with a cancellation token.
    /// Probes each serial port to identify DAQiFi devices.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>A task containing the collection of discovered DAQiFi devices.</returns>
    public override async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Prevent concurrent discovery operations
        await DiscoverySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discoveredDevices = new List<IDeviceInfo>();
            // Pre-filter by USB VID/PID where the platform supports it
            // (closes #157). This drops discovery time from ~1 minute on a
            // typical Windows system (10+ COM ports each timing out at ~5s)
            // to <1s by skipping every non-DAQiFi port without opening it.
            // It also stops the previous behavior of sending SCPI commands
            // to other vendors' COM ports (Bluetooth radios, GPS, etc.).
            // Ports the descriptor provider can't classify fall through to
            // legacy probing, matched only by name-pattern in FilterProbableDaqifiPorts.
            var allPorts = _portNameProvider?.Invoke()
                ?? SerialStreamTransport.GetAvailablePortNames();
            var nameFilteredPorts = FilterProbableDaqifiPorts(allPorts);
            var availablePorts = FilterByUsbDescriptor(nameFilteredPorts).ToList();

            // Probe candidate ports in parallel (issue #157). Each SerialPort
            // is its own physical resource so concurrent opens are safe; the
            // VID/PID pre-filter typically leaves only 0-1 candidates anyway,
            // but the legacy fallback (null descriptor) can still surface
            // multiple unclassifiable ports — probing them sequentially adds
            // ~1.2s per port (DeviceWakeUpDelayMs + ResponseTimeoutMs).
            //
            // Cap concurrency at MaxParallelProbes so a 20-port system with
            // a missing descriptor provider doesn't open 20 serial handles at
            // once — most platforms throttle quietly above ~8 concurrent opens.
            //
            // The gate is taken INSIDE ProbeSafelyAsync, around the port open only.
            // Holding it across the whole call would let the abandoned-claim drain
            // wait — which opens nothing and merely awaits an already-running task —
            // occupy a slot, so a pass whose predecessor timed out with every slot
            // busy could park all MaxParallelProbes slots on drain waits and delay a
            // healthy port behind them (Qodo #454 pass 1 #2).
            var maxConcurrency = Math.Max(1, Math.Min(MaxParallelProbes, availablePorts.Count));
            using var probeGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            var probeTasks = availablePorts.Select(async portName =>
            {
                var deviceInfo = await ProbeSafelyAsync(portName, probeGate, cancellationToken).ConfigureAwait(false);
                if (deviceInfo != null)
                {
                    // Raise per-probe rather than after Task.WhenAll: one hung port
                    // must not silence every healthy device on the system (#294).
                    // Fires on a worker thread, possibly concurrently with other
                    // probes' events — handlers own their thread marshaling (both
                    // desktop apps already dispatch to their UI thread).
                    OnDeviceDiscovered(deviceInfo);
                }
                return deviceInfo;
            }).ToList();

            // Catch OCE here so the DiscoveryCompleted event always fires
            // (callers expect a "discovery pass terminated" signal regardless
            // of how it ended). ProbeSafelyAsync rethrows OCE on caller
            // cancellation so WhenAll short-circuits the rest of the set —
            // we then settle for whatever probes already finished cleanly.
            IDeviceInfo?[]? probeResults = null;
            try
            {
                probeResults = await Task.WhenAll(probeTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Drain any tasks that did finish successfully before cancellation.
                probeResults = probeTasks
                    .Where(t => t.Status == TaskStatus.RanToCompletion)
                    .Select(t => t.Result)
                    .ToArray();
            }

            // Events already fired per-probe above; this loop only aggregates the
            // return value.
            foreach (var deviceInfo in probeResults)
            {
                if (deviceInfo != null)
                {
                    discoveredDevices.Add(deviceInfo);
                }
            }

            OnDiscoveryCompleted();
            return discoveredDevices;
        }
        finally
        {
            DiscoverySemaphore.Release();
        }
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Attempts to probe a serial port and retrieve device information.
    /// Opens the port, sends GetDeviceInfo command, and waits for a status response.
    /// </summary>
    /// <param name="portName">The serial port name.</param>
    /// <param name="probeGate">
    /// Caps concurrent port opens for the sweep. Held only around the probe itself,
    /// never around claim acquisition or the abandoned-claim drain wait — neither
    /// opens a port, and holding a slot through them would throttle healthy ports
    /// behind ports that are merely draining.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Device info if a DAQiFi device responds, null otherwise — including
    /// when the port is claimed by a probe that is still genuinely in flight.
    /// Swallows non-cancellation exceptions so a single failing port doesn't tear
    /// down the whole concurrent probe pass; cancellation propagates so Task.WhenAll
    /// short-circuits when the caller's token is canceled.</returns>
    private async Task<IDeviceInfo?> ProbeSafelyAsync(
        string portName, SemaphoreSlim probeGate, CancellationToken cancellationToken)
    {
        PortProbeClaim? claim = null;
        var claimHandedToContinuation = false;
        try
        {
            // Claim the port BEFORE probing (atomic): at most one probe may be in
            // flight or abandoned per port process-wide, so one wedged port costs at
            // most ONE blocked thread per TTL window regardless of how many finder
            // instances sweep concurrently.
            claim = new PortProbeClaim();
            var claimed = false;
            var drainWaited = false;
            // Up to three attempts. Attempt 2 handles exactly one stale-completed
            // claim (its release continuation raced us) by clearing it and
            // re-claiming immediately, so an available port isn't skipped for a
            // whole sweep — that miss is invisible to the 2-3s desktop sweeps but
            // real for one-shot MCP discovery calls (Qodo pass 5 #3). Attempt 3
            // covers the drain wait below, whose worst path is
            // wait -> observe completed claim -> clear -> claim.
            for (var attempt = 0; attempt < 3 && !claimed; attempt++)
            {
                var existing = PortClaims.GetOrAdd(portName, claim);
                if (ReferenceEquals(existing, claim))
                {
                    claimed = true;
                    break;
                }

                if (existing.Probe is { IsCompleted: true })
                {
                    // Stale completed claim — clear it and retry the claim once.
                    ReleaseClaimIfOwned(portName, existing);
                    continue;
                }

                var abandonedAt = existing.AbandonedAtTicks;
                if (abandonedAt >= 0)
                {
                    var abandonedForMs = Environment.TickCount64 - abandonedAt;
                    if (abandonedForMs >= QuarantineRetryTtlMs
                        && PortClaims.TryUpdate(portName, claim, existing))
                    {
                        // TTL elapsed on a still-stuck claim — we won the retry.
                        claimed = true;
                        break;
                    }

                    // Freshly abandoned: the PREVIOUS pass ended by timeout or
                    // caller cancellation and left this claim behind while its
                    // probe unwinds — measured ~490ms, not wedged. The
                    // skip below costs ~1ms, so a caller that retries promptly
                    // (the MCP DaqifiAgent builds a fresh finder per call) would
                    // burn every retry inside the drain window and conclude no
                    // device exists. Wait for the claim to clear instead, bounded
                    // by both the drain window and the caller's own budget.
                    //
                    // This starts NO new probe and blocks NO new thread — we await
                    // the EXISTING probe task — so the one-blocked-thread-per-port
                    // bound from #294/#295 is untouched. A genuinely wedged port
                    // just burns the remaining window once and then ages out of it.
                    var pending = existing.Probe;
                    // Long arithmetic on purpose: abandonedForMs is unbounded (a
                    // claim whose TTL retry lost the race can be arbitrarily old),
                    // so narrowing it first could overflow into a large positive
                    // budget. Computed this way the result is always
                    // <= AbandonedClaimDrainWaitMs, making the cast below safe.
                    var drainBudgetMs = AbandonedClaimDrainWaitMs - abandonedForMs;
                    if (!drainWaited && pending != null && drainBudgetMs > 0
                        && !cancellationToken.IsCancellationRequested)
                    {
                        drainWaited = true;
                        using (var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            drainCts.CancelAfter((int)drainBudgetMs);
                            try
                            {
                                await pending.WaitAsync(drainCts.Token).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Drain window elapsed, caller cancelled, or the
                                // abandoned probe faulted. Its own continuation
                                // already observed any fault; all we needed was the
                                // "no longer in flight" edge. Re-check below.
                            }
                        }

                        // Surface caller cancellation rather than reporting the port
                        // as absent (mirrors the hard-timeout path below).
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }
                }

                // In flight elsewhere, still wedged past the drain window but inside
                // the TTL, or another instance won the race — the port is spoken for.
                return null;
            }
            if (!claimed)
            {
                return null;
            }

            var probe = _probeOverride ?? TryGetDeviceInfoAsync;

            // Take a probe slot only now. Claim acquisition and the drain wait above
            // open no port, so gating them would just throttle healthy ports behind
            // ports that are draining. Released the moment this probe settles or is
            // abandoned — an abandoned probe holds no slot on later sweeps either.
            await probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Task.Run: SerialPort.Open() (and DiscardInBuffer etc.) are synchronous
                // blocking I/O that would otherwise run on the CALLING thread up to the
                // probe's first await — in the desktop apps that thread is the UI thread,
                // and a slow or stuck open froze the whole window (#294). Pass
                // CancellationToken.None to Task.Run itself: the inner token still
                // cancels the probe's delays; we never want "cancelled before start"
                // to look like a probe fault.
                var probeTask = Task.Run(() => probe(portName, cancellationToken), CancellationToken.None);
                claim.Probe = probeTask;

                // Hard per-port ceiling: a wedged CDC device hangs Open() inside native
                // GetCommState with no exception, so the catch below never fires and,
                // pre-#294, Task.WhenAll in DiscoverAsync never settled. Open() is
                // uncancellable, so on timeout the stuck task is ABANDONED — its own
                // finally block closes the port if the kernel ever completes the I/O.
                var winner = await Task.WhenAny(
                    probeTask,
                    Task.Delay(PortProbeHardTimeoutMs, cancellationToken)).ConfigureAwait(false);

                if (winner != probeTask)
                {
                    if (probeTask.IsCompletedSuccessfully)
                    {
                        // The probe finished right at the timeout/cancellation boundary —
                        // honor DiscoverAsync's "settle for whatever probes already
                        // finished cleanly" contract instead of discarding a completed
                        // result (Qodo pass 5 #4). The finally releases our claim.
                        return await probeTask.ConfigureAwait(false);
                    }

                    // Whether the wait ended by timeout or by caller cancellation, the
                    // uncancellable probe may still be blocked in native I/O — mark the
                    // claim abandoned BEFORE surfacing cancellation, so a cancelled sweep
                    // can't walk away from an untracked probe (Qodo PR #295 pass 2 #1).
                    claim.AbandonedAtTicks = Environment.TickCount64;
                    claimHandedToContinuation = true;

                    // When the abandoned I/O finally completes (any way), observe its
                    // fault so it can't surface as an UnobservedTaskException, and release
                    // the claim — atomically only if it's still ours (a TTL retry may have
                    // replaced it with a newer attempt that must survive).
                    var abandonedClaim = claim;
                    _ = probeTask.ContinueWith(
                        t =>
                        {
                            _ = t.Exception;
                            ReleaseClaimIfOwned(portName, abandonedClaim);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    // Surface caller cancellation only after tracking the abandoned probe.
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }

                return await probeTask.ConfigureAwait(false);
            }
            finally
            {
                probeGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller asked to stop — propagate so Task.WhenAll observes the
            // cancellation and short-circuits the rest of the probe set.
            throw;
        }
        catch
        {
            // Probe failure (port locked, no response, IO error, etc.) is
            // expected for non-DAQiFi serial devices — keep the rest of the
            // concurrent probe set going and return no device for this port.
            return null;
        }
        finally
        {
            // Release our claim unless the abandonment continuation now owns it
            // (it releases when the stuck I/O eventually completes). Claims we
            // never won (ReferenceEquals miss) were never inserted under our
            // identity, so ReleaseClaimIfOwned is a no-op for them.
            if (claim != null && !claimHandedToContinuation)
            {
                ReleaseClaimIfOwned(portName, claim);
            }
        }
    }

    /// <param name="portName">The serial port name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Device info if a DAQiFi device responds, null otherwise.</returns>
    private async Task<IDeviceInfo?> TryGetDeviceInfoAsync(string portName, CancellationToken cancellationToken)
    {
        SerialPort? port = null;
        MessageProducer<string>? producer = null;
        StreamMessageConsumer<DaqifiOutMessage>? consumer = null;

        try
        {
            // Create and configure the serial port
            port = new SerialPort(portName, _baudRate)
            {
                ReadTimeout = ProbeTimeoutMs,
                WriteTimeout = ProbeTimeoutMs
            };

            // Try to open the port
            port.Open();
            port.DtrEnable = true;

            // Wait for device to wake up (devices need time after DTR is enabled)
            await Task.Delay(DeviceWakeUpDelayMs, cancellationToken).ConfigureAwait(false);

            // Set up message producer and consumer
            var stream = port.BaseStream;
            producer = new MessageProducer<string>(stream);
            producer.Start();

            var parser = new ProtobufMessageParser();
            consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);

            // Track status message reception
            DaqifiOutMessage? statusMessage = null;
            var messageReceived = new TaskCompletionSource<bool>();

            consumer.MessageReceived += (_, args) =>
            {
                if (args.Message.Data is DaqifiOutMessage message)
                {
                    var messageType = ProtobufProtocolHandler.DetectMessageType(message);
                    if (messageType == ProtobufMessageType.Status)
                    {
                        statusMessage = message;
                        messageReceived.TrySetResult(true);
                    }
                }
            };

            consumer.Start();

            // Identity probe: GetDeviceInfo only. The rest of the old setup sequence
            // (StopStreaming / TurnDeviceOn / SetProtobufStreamFormat) is connection setup, not
            // identification — a healthy DAQiFi answers SYSTem:SYSInfoPB? regardless of stream format or
            // power state, so the caller setting up an actual connection still owns those. Echo is left
            // alone too: an echo-on device wraps its reply in the echoed command text plus a trailing
            // "DAQIFI>" prompt, but ProtobufMessageParser now resyncs past that non-protobuf noise to
            // the embedded frame (issue #268), so the probe stays GetDeviceInfo-only (closes #157)
            // instead of toggling device echo.
            // Send GetDeviceInfo command with retry logic
            var timeout = DateTime.UtcNow.AddMilliseconds(ResponseTimeoutMs);
            var lastRequestTime = DateTime.MinValue;
            var retryCount = 0;

            while (statusMessage == null && DateTime.UtcNow < timeout && !cancellationToken.IsCancellationRequested)
            {
                // Send request every RetryIntervalMs, up to MaxRetries times
                if ((DateTime.UtcNow - lastRequestTime).TotalMilliseconds >= RetryIntervalMs && retryCount < MaxRetries)
                {
                    producer.Send(ScpiMessageProducer.GetDeviceInfo);
                    lastRequestTime = DateTime.UtcNow;
                    retryCount++;
                }

                // Wait a bit for response
                var remainingTime = Math.Min(PollIntervalMs, (int)(timeout - DateTime.UtcNow).TotalMilliseconds);
                if (remainingTime > 0)
                {
                    await Task.WhenAny(
                        messageReceived.Task,
                        Task.Delay(remainingTime, cancellationToken)).ConfigureAwait(false);
                }
            }

            if (statusMessage == null)
            {
                return null; // Not a DAQiFi device or device not responding
            }

            string? locationKey;
            try
            {
                locationKey = _usbLocationProvider.GetLocationKey(portName);
            }
            catch
            {
                // Location resolution is enrichment metadata, not identification — a
                // misbehaving custom IUsbLocationProvider must never discard an otherwise
                // successfully-probed device by throwing into the broad catch below.
                // Mirrors FilterByUsbDescriptor's handling of a throwing
                // IUsbPortDescriptorProvider.
                locationKey = null;
            }

            return BuildDeviceInfo(statusMessage, portName, locationKey);
        }
        catch (UnauthorizedAccessException)
        {
            // Port is in use by another application
            return null;
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled (e.g. during the wake / echo-disable delays). Propagate so the
            // DiscoverAsync Task.WhenAll cancellation path runs — don't convert it into a probe-miss.
            throw;
        }
        catch (Exception)
        {
            // Any other error - not a valid DAQiFi device or port unavailable
            return null;
        }
        finally
        {
            // Clean up resources
            try
            {
                consumer?.StopSafely(500);
                consumer?.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                producer?.StopSafely(500);
                producer?.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                if (port is { IsOpen: true })
                {
                    port.DtrEnable = false;
                    await Task.Delay(50).ConfigureAwait(false); // Give DTR time to be processed
                    port.Close();
                }

                port?.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Filters the post-name-filter ports down to those whose USB descriptor
    /// matches a known DAQiFi VID/PID. Ports the provider can't classify
    /// (returns null) fall through to probing — preserves cross-platform
    /// behavior where the provider doesn't have a real impl.
    /// </summary>
    private IEnumerable<string> FilterByUsbDescriptor(IEnumerable<string> ports)
    {
        foreach (var port in ports)
        {
            UsbPortDescriptor? descriptor;
            try
            {
                descriptor = _usbPortDescriptorProvider.GetDescriptor(port);
            }
            catch
            {
                // A misbehaving descriptor provider must never block discovery.
                // The shipped providers already swallow their own errors, but
                // a custom IUsbPortDescriptorProvider could throw — fall back
                // to legacy probing rather than aborting the whole scan.
                descriptor = null;
            }

            if (descriptor == null)
            {
                // No classification available — preserve legacy behavior
                // and probe the port (caller's name filter has already
                // removed the obvious non-candidates).
                yield return port;
                continue;
            }
            if (DaqifiUsbIds.IsDaqifiCdcDevice(descriptor))
            {
                yield return port;
            }
            // else: not a DAQiFi device — skip without opening / probing.
        }
    }

    /// <summary>
    /// Filters the list of available ports to only include those likely to be DAQiFi devices.
    /// Excludes debug ports, Bluetooth ports, and on macOS prefers /dev/cu.* over /dev/tty.*.
    /// </summary>
    /// <param name="allPorts">All available serial port names.</param>
    /// <returns>Filtered list of ports to probe.</returns>
    private static IEnumerable<string> FilterProbableDaqifiPorts(string[] allPorts)
    {
        // Skip debug and bluetooth ports which are unlikely to be DAQiFi devices
        var excludePatterns = new[]
        {
            "debug",
            "Bluetooth",
            "wlan"
        };

        var filteredPorts = allPorts
            .Where(port => !excludePatterns.Any(pattern =>
                port.Contains(pattern, StringComparison.OrdinalIgnoreCase)));

        // On macOS/Linux, prefer /dev/cu.* ports (for outgoing connections) over /dev/tty.* ports
        // If we have both cu and tty versions of the same port, only use cu
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var cuPorts = filteredPorts.Where(p => p.StartsWith("/dev/cu.")).ToList();
            var ttyPorts = filteredPorts.Where(p => p.StartsWith("/dev/tty.")).ToList();

            // For each tty port, check if there's a corresponding cu port
            // If so, skip the tty port
            var portsToProbe = new List<string>(cuPorts);
            foreach (var ttyPort in ttyPorts)
            {
                var cuEquivalent = ttyPort.Replace("/dev/tty.", "/dev/cu.");
                if (!cuPorts.Contains(cuEquivalent))
                {
                    portsToProbe.Add(ttyPort);
                }
            }

            // Add any non-tty/cu ports (like /dev/ttyUSB0, /dev/ttyACM0)
            portsToProbe.AddRange(filteredPorts.Where(p =>
                !p.StartsWith("/dev/cu.") && !p.StartsWith("/dev/tty.")));

            return portsToProbe;
        }

        return filteredPorts;
    }

    /// <summary>
    /// Converts from the Device.DeviceType enum to the Discovery.DeviceType enum.
    /// </summary>
    /// <param name="deviceType">The Device namespace DeviceType.</param>
    /// <returns>The Discovery namespace DeviceType.</returns>
    internal static DeviceType ConvertDeviceType(Device.DeviceType deviceType)
    {
        return deviceType switch
        {
            Device.DeviceType.Nyquist1 => DeviceType.Nyquist1,
            Device.DeviceType.Nyquist2 => DeviceType.Nyquist2,
            Device.DeviceType.Nyquist3 => DeviceType.Nyquist3,
            _ => DeviceType.Unknown
        };
    }

    /// <summary>
    /// Maps a device's status message to <see cref="DeviceInfo"/>. Pure mapping logic split out
    /// from <see cref="TryGetDeviceInfoAsync"/> (which owns the SerialPort probe and any
    /// IUsbLocationProvider error handling) so it can be unit tested directly with a
    /// hand-constructed <see cref="DaqifiOutMessage"/> — no real serial port required.
    /// </summary>
    /// <param name="statusMessage">The device's SYSInfo status response.</param>
    /// <param name="portName">The serial port name the device was probed on.</param>
    /// <param name="locationKey">The already-resolved USB physical-location key, or null.</param>
    internal static DeviceInfo BuildDeviceInfo(DaqifiOutMessage statusMessage, string portName, string? locationKey)
    {
        return new DeviceInfo
        {
            Name = !string.IsNullOrWhiteSpace(statusMessage.DevicePn)
                ? statusMessage.DevicePn
                : portName,
            SerialNumber = statusMessage.DeviceSn.ToString(),
            FirmwareVersion = statusMessage.DeviceFwRev ?? "Unknown",
            ConnectionType = ConnectionType.Serial,
            PortName = portName,
            Type = ConvertDeviceType(DeviceTypeDetector.DetectFromPartNumber(statusMessage.DevicePn)),
            IsPowerOn = true,
            LocationKey = locationKey
        };
    }

    #endregion
}
