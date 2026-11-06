using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Firmware;

/// <summary>
/// How <see cref="LanChipInfoProviderExtensions.GetLanChipInfoWithRetryAsync"/> spends its
/// retry budget.
/// </summary>
/// <remarks>
/// The defaults are the ones Core's own WiFi firmware-status check uses
/// (<see cref="FirmwareUpdateServiceOptions.LanChipInfoMaxAttempts"/> and friends), so a consumer
/// that just wants "what Core does" can pass nothing at all.
/// </remarks>
public sealed record LanChipInfoRetryOptions
{
    /// <summary>
    /// Gets the maximum number of chip-info queries to make. Values below 1 are treated as 1 —
    /// the probe always makes at least one attempt.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Gets the pause between attempts. Not applied after the final attempt. Negative values —
    /// including <see cref="Timeout.InfiniteTimeSpan"/> — are treated as no pause at all.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="TotalTimeout"/>, <see cref="Timeout.InfiniteTimeSpan"/> is not honored
    /// here: an infinite <i>pause</i> inside a bounded retry has no meaning, and with an equally
    /// unbounded budget nothing would ever release it. Use <see cref="MaxAttempts"/> = 1 to say
    /// "don't retry".
    /// </remarks>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the wall-clock ceiling for the whole probe, including the per-attempt device
    /// timeouts and the retry delays.
    /// </summary>
    /// <remarks>
    /// Needed because <see cref="MaxAttempts"/> × the device's own response timeout plus the
    /// delays can far exceed what the caller intended to spend — and this probe usually runs
    /// while an operation lock is held. Expiry ends the probe with the same "unavailable"
    /// result as an exhausted attempt count rather than throwing; only the caller's own
    /// cancellation surfaces as <see cref="OperationCanceledException"/>.
    /// A non-positive value leaves no budget at all, so no attempt is made — with the single
    /// exception of <see cref="Timeout.InfiniteTimeSpan"/>, which means the opposite: no ceiling.
    /// </remarks>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Gets a value indicating whether to send a single <c>LAN:APPLY</c> when the device reports
    /// its WINC state machine is not initialized (SCPI <c>-200</c>).
    /// </summary>
    /// <remarks>
    /// Sent at most once per probe: repeatedly kicking APPLY would tear down and re-initialize
    /// the WINC on every failed attempt, which risks disrupting an already-associated WiFi link
    /// for no additional benefit. Only possible when the provider is also the connected
    /// <see cref="IStreamingDevice"/> — there is nothing to send the command through otherwise.
    /// </remarks>
    public bool KickLanApplyOnNotInitialized { get; init; } = true;

    /// <summary>
    /// Gets an optional sink that receives one <see cref="LanChipInfoProbeProgress"/> for every
    /// attempt that does not produce chip info, so a caller can show that the probe is retrying
    /// rather than stalled. <see langword="null"/> — the default — reports nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe can occupy roughly ten seconds with the defaults (<see cref="MaxAttempts"/>
    /// attempts, <see cref="RetryDelay"/> apart, on top of the device's own per-query timeout),
    /// and the module it waits on is legitimately unready for seconds at a time (#144, #203).
    /// Without this, the only per-attempt signal is <see cref="Microsoft.Extensions.Logging.ILogger"/>
    /// debug output — a diagnostic channel, not one a UI should drive a status line from (#745).
    /// </para>
    /// <para>
    /// The sink lives here rather than as a parameter on
    /// <see cref="LanChipInfoProviderExtensions.GetLanChipInfoWithRetryAsync"/> because the
    /// method's signature shipped in v1.8.0 and ADR 0002 promises source compatibility: a new
    /// parameter before <c>cancellationToken</c> breaks every caller that passes the token
    /// positionally, which is how Core's own WiFi status check calls it. This mirrors
    /// <see cref="Daqifi.Core.Device.SdCard.SdCardParseOptions.Progress"/>.
    /// </para>
    /// <para>
    /// Reporting is best-effort: an exception from the sink is swallowed and the probe carries on.
    /// A handler cannot fail an operation whose whole contract is that failures come back as a
    /// result.
    /// </para>
    /// </remarks>
    public IProgress<LanChipInfoProbeProgress>? Progress { get; init; }
}

/// <summary>
/// One attempt of a bounded chip-info probe that did not produce chip info, reported to
/// <see cref="LanChipInfoRetryOptions.Progress"/> so a caller can render "retrying".
/// </summary>
/// <param name="Attempt">The 1-based number of the attempt that just failed.</param>
/// <param name="MaxAttempts">
/// The total number of attempts the probe is allowed, after the normalization
/// <see cref="LanChipInfoRetryOptions.MaxAttempts"/> documents. Equal to
/// <paramref name="Attempt"/> on the last one.
/// </param>
/// <param name="WasLanNotInitialized">
/// Whether <i>this</i> attempt failed with <see cref="LanNotInitializedException"/> — the device
/// reporting its WINC state machine has not reached INITIALIZED (SCPI <c>-200</c>). False for a
/// null answer or any other failure, so it describes the attempt being reported rather than the
/// worst one seen so far.
/// </param>
/// <param name="RetryDelay">
/// The pause that follows before the next attempt, after the normalization
/// <see cref="LanChipInfoRetryOptions.RetryDelay"/> documents.
/// <see cref="TimeSpan.Zero"/> on the last attempt, because no retry follows it — which is also
/// how a caller tells "retrying" from "that was the last try".
/// </param>
public readonly record struct LanChipInfoProbeProgress(
    int Attempt,
    int MaxAttempts,
    bool WasLanNotInitialized,
    TimeSpan RetryDelay);

/// <summary>
/// The outcome of a bounded chip-info probe: what was read, and — when nothing was — whether the
/// device was specifically reporting an uninitialized WINC state machine.
/// </summary>
/// <param name="ChipInfo">The chip info that was read, or <see langword="null"/> if the probe never got one.</param>
/// <param name="WasLanNotInitialized">
/// Whether the <i>final</i> failure was a <see cref="LanNotInitializedException"/>. Reset by any
/// other kind of failure, so it describes the terminal condition rather than an earlier attempt.
/// Meaningless when <see cref="ChipInfo"/> is non-null.
/// </param>
public readonly record struct LanChipInfoProbeResult(LanChipInfo? ChipInfo, bool WasLanNotInitialized)
{
    /// <summary>Gets a value indicating whether the probe read the chip info.</summary>
    public bool Succeeded => ChipInfo is not null;
}

/// <summary>
/// Retry helpers for <see cref="ILanChipInfoProvider"/>.
/// </summary>
public static class LanChipInfoProviderExtensions
{
    /// <summary>
    /// Queries the device's WiFi chip info with a bounded retry, so a transiently-unready WINC
    /// module reports what it actually is rather than "unavailable".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single <see cref="ILanChipInfoProvider.GetLanChipInfoAsync"/> is not a reliable answer to
    /// "what WiFi firmware is on this device". Right after a PIC32 firmware update the application
    /// is up while WiFi is still finishing startup (#144), and a module whose state machine has not
    /// reached INITIALIZED answers SCPI <c>-200</c> instead of JSON (#203). Both clear on their own
    /// within seconds. Treating the first failure as the answer is what sends a caller into a
    /// needless multi-minute WiFi reflash of already-current firmware — which is exactly why every
    /// consumer ended up hand-rolling this loop (issue #269).
    /// </para>
    /// <para>
    /// This is the same probe Core runs inside
    /// <see cref="IFirmwareUpdateService.CheckWifiFirmwareStatusAsync"/>, exposed so consumers share
    /// one implementation instead of reimplementing it.
    /// </para>
    /// <para>
    /// Failures are absorbed: an exhausted budget comes back as a result with a null
    /// <see cref="LanChipInfoProbeResult.ChipInfo"/>, not an exception — the caller decides what an
    /// unreadable module means. The one exception that does propagate is the caller's own
    /// cancellation. That extends to a misconfigured budget: out-of-range option values are
    /// normalized to their documented meaning (see <see cref="LanChipInfoRetryOptions"/>) rather
    /// than thrown back at the caller from partway through the loop.
    /// </para>
    /// <para>
    /// Because the probe can occupy roughly ten seconds, <see cref="LanChipInfoRetryOptions.Progress"/>
    /// reports each attempt that does not produce chip info, so a caller can show that it is
    /// retrying rather than stalled (#745). A probe that reads the module on its first attempt
    /// reports nothing. An attempt the wall-clock budget cuts short is not reported either: it ends
    /// the probe, and the returned result is what describes it.
    /// </para>
    /// </remarks>
    /// <param name="provider">The device to query. When it is also an <see cref="IStreamingDevice"/>, it is what the <c>LAN:APPLY</c> kick is sent through.</param>
    /// <param name="options">The retry budget and the optional per-attempt progress sink; <see langword="null"/> uses <see cref="LanChipInfoRetryOptions"/>'s defaults, which report nothing.</param>
    /// <param name="logger">Optional logger for per-attempt diagnostics.</param>
    /// <param name="cancellationToken">Cancels the probe. Unlike the internal budget, this surfaces as <see cref="OperationCanceledException"/>.</param>
    /// <returns>What was read, and how the probe ended if nothing was.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public static async Task<LanChipInfoProbeResult> GetLanChipInfoWithRetryAsync(
        this ILanChipInfoProvider provider,
        LanChipInfoRetryOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var effectiveOptions = options ?? new LanChipInfoRetryOptions();
        var log = logger ?? NullLogger.Instance;

        // The APPLY kick needs a way to reach the device. In practice the provider is the device
        // — DaqifiStreamingDevice implements both — and when it isn't, there is simply no kick.
        var device = provider as IStreamingDevice;

        // The options are a plain settable record a consumer can get wrong, so every value is
        // normalized into something the loop can spend rather than passed straight to a BCL call
        // that would throw — this probe's contract is that failures come back as an unavailable
        // result, and that has to hold for a misconfigured budget too.
        var maxAttempts = Math.Max(1, effectiveOptions.MaxAttempts);
        var retryDelay = NormalizeRetryDelay(effectiveOptions.RetryDelay);
        var totalTimeout = NormalizeTotalTimeout(effectiveOptions.TotalTimeout);

        // Tracks the most recent failure's classification (reset on any non-LanNotInitialized
        // outcome) so the caller can report the specific not-initialized condition only when that
        // was genuinely the terminal one, not stale from an earlier attempt.
        var lastFailureWasLanNotInitialized = false;
        var hasSentLanApply = false;

        // Linking the caller's token preserves cancellation semantics; the timeout CTS just adds a
        // deadline, and the `when` filters below tell the two apart so only the caller's
        // cancellation is allowed to escape.
        using var timeoutCts = new CancellationTokenSource(totalTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                linkedToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                log.LogDebug(
                    "LAN chip-info probe hit total timeout ({Timeout}) before attempt {Attempt}/{Max}.",
                    totalTimeout,
                    attempt,
                    maxAttempts);
                return new LanChipInfoProbeResult(null, lastFailureWasLanNotInitialized);
            }

            try
            {
                var chipInfo = await provider.GetLanChipInfoAsync(linkedToken).ConfigureAwait(false);
                if (chipInfo != null)
                {
                    if (attempt > 1)
                    {
                        log.LogDebug(
                            "LAN chip-info query succeeded on attempt {Attempt}/{Max}.",
                            attempt,
                            maxAttempts);
                    }
                    return new LanChipInfoProbeResult(chipInfo, false);
                }
                lastFailureWasLanNotInitialized = false;
                log.LogDebug(
                    "LAN chip-info query returned null on attempt {Attempt}/{Max}.",
                    attempt,
                    maxAttempts);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                log.LogDebug(
                    "LAN chip-info probe hit total timeout ({Timeout}) during attempt {Attempt}/{Max}.",
                    totalTimeout,
                    attempt,
                    maxAttempts);
                return new LanChipInfoProbeResult(null, lastFailureWasLanNotInitialized);
            }
            catch (LanNotInitializedException ex)
            {
                lastFailureWasLanNotInitialized = true;
                log.LogDebug(
                    ex,
                    "LAN chip-info query on attempt {Attempt}/{Max} reported the WINC state machine is not initialized.",
                    attempt,
                    maxAttempts);

                if (effectiveOptions.KickLanApplyOnNotInitialized && !hasSentLanApply && device is { IsConnected: true })
                {
                    // Observe cancellation before this state-changing Send: a cancelled probe must
                    // not still kick APPLY on the device. Uses the caller's token (not the linked
                    // timeout token) so a total-timeout expiry alone doesn't suppress a kick the
                    // caller never actually asked to cancel.
                    cancellationToken.ThrowIfCancellationRequested();

                    hasSentLanApply = true;
                    try
                    {
                        device.Send(ScpiMessageProducer.ApplyNetworkLan);
                        log.LogDebug("Sent LAN:APPLY to initialize the WINC state machine after a not-initialized chip-info response.");
                    }
                    catch (Exception sendEx) when (sendEx is not OperationCanceledException)
                    {
                        // Best-effort: falling through to the normal retry delay/loop below still
                        // gives the device a chance to recover on its own.
                        log.LogDebug(sendEx, "Failed to send LAN:APPLY after a not-initialized chip-info response; continuing retry loop without it.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailureWasLanNotInitialized = false;
                log.LogDebug(
                    ex,
                    "LAN chip-info query failed on attempt {Attempt}/{Max}.",
                    attempt,
                    maxAttempts);
            }

            // Every failure branch above converges here: the attempt produced no chip info and the
            // probe is still running normally. One report per such attempt, in order, which is the
            // span a caller needs to render "retrying". The total-timeout branches above return
            // instead of falling through, so they report nothing — they end the probe, and the
            // result they return is what describes that.
            ReportAttemptFailed(
                effectiveOptions.Progress,
                log,
                new LanChipInfoProbeProgress(
                    attempt,
                    maxAttempts,
                    lastFailureWasLanNotInitialized,
                    attempt < maxAttempts ? retryDelay : TimeSpan.Zero));

            if (attempt < maxAttempts)
            {
                try
                {
                    await Task.Delay(retryDelay, linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    log.LogDebug(
                        "LAN chip-info probe hit total timeout ({Timeout}) during retry delay after attempt {Attempt}/{Max}.",
                        totalTimeout,
                        attempt,
                        maxAttempts);
                    return new LanChipInfoProbeResult(null, lastFailureWasLanNotInitialized);
                }
            }
        }

        log.LogDebug(
            "LAN chip-info query exhausted {Max} attempts; reporting the module as unreadable (not-initialized: {NotInitialized}).",
            maxAttempts,
            lastFailureWasLanNotInitialized);
        return new LanChipInfoProbeResult(null, lastFailureWasLanNotInitialized);
    }

    /// <summary>
    /// Hands one failed attempt to the caller's progress sink without letting the sink affect the
    /// probe.
    /// </summary>
    /// <remarks>
    /// Every exception is swallowed, <see cref="OperationCanceledException"/> included. A consumer
    /// of <see cref="LanChipInfoRetryOptions.Progress"/> is drawing a status line; it must not be
    /// able to fail an operation whose documented contract is that failures come back as an
    /// unavailable result, and it must not discard a chip-info read the remaining attempts may
    /// still make. The caller's own <see cref="CancellationToken"/> remains the only thing that
    /// stops the loop, and the next iteration observes it.
    /// </remarks>
    private static void ReportAttemptFailed(
        IProgress<LanChipInfoProbeProgress>? progress,
        ILogger log,
        LanChipInfoProbeProgress report)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            progress.Report(report);
        }
        catch (Exception ex)
        {
            log.LogDebug(
                ex,
                "A LAN chip-info progress handler threw on attempt {Attempt}/{Max}; ignoring it and continuing the probe.",
                report.Attempt,
                report.MaxAttempts);
        }
    }

    /// <summary>
    /// Clamps the retry pause to something <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// accepts.
    /// </summary>
    /// <remarks>
    /// Every negative value collapses to "no pause", <see cref="Timeout.InfiniteTimeSpan"/>
    /// included. Other negatives would throw <see cref="ArgumentOutOfRangeException"/> partway
    /// through a probe documented to absorb failures, and the infinite sentinel would stall the
    /// caller between attempts until something else cancelled — forever, when
    /// <see cref="LanChipInfoRetryOptions.TotalTimeout"/> is unbounded too. Both are
    /// misconfigurations, and retrying immediately is the bounded reading of them.
    /// </remarks>
    private static TimeSpan NormalizeRetryDelay(TimeSpan retryDelay) =>
        retryDelay < TimeSpan.Zero ? TimeSpan.Zero : retryDelay;

    /// <summary>
    /// Clamps the wall-clock budget to something <see cref="CancellationTokenSource"/> accepts.
    /// </summary>
    /// <remarks>
    /// <see cref="Timeout.InfiniteTimeSpan"/> is kept as the one meaningful negative: it is how
    /// .NET spells "no ceiling" and <see cref="CancellationTokenSource"/> already reads it that
    /// way. Any other negative becomes <see cref="TimeSpan.Zero"/> — the "no budget, so no
    /// attempt" outcome the option documents — instead of the
    /// <see cref="ArgumentOutOfRangeException"/> the constructor would throw.
    /// </remarks>
    private static TimeSpan NormalizeTotalTimeout(TimeSpan totalTimeout) =>
        totalTimeout < TimeSpan.Zero && totalTimeout != Timeout.InfiniteTimeSpan
            ? TimeSpan.Zero
            : totalTimeout;
}
