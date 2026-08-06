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
    /// Gets the pause between attempts. Not applied after the final attempt.
    /// </summary>
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
    /// A non-positive value leaves no budget at all, so no attempt is made.
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
}

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
    /// cancellation.
    /// </para>
    /// </remarks>
    /// <param name="provider">The device to query. When it is also an <see cref="IStreamingDevice"/>, it is what the <c>LAN:APPLY</c> kick is sent through.</param>
    /// <param name="options">The retry budget; <see langword="null"/> uses <see cref="LanChipInfoRetryOptions"/>'s defaults.</param>
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

        var maxAttempts = Math.Max(1, effectiveOptions.MaxAttempts);
        var retryDelay = effectiveOptions.RetryDelay;
        var totalTimeout = effectiveOptions.TotalTimeout;

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
}
