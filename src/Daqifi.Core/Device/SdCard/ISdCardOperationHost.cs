using Daqifi.Core.Device.Internal;
using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// The extra slice of a streaming device that <see cref="SdCardOperations"/> needs on top of
/// <see cref="IDeviceOperationHost"/>: whether the link is USB, the two SD transfer budgets,
/// and the low-space event.
/// </summary>
/// <remarks>
/// <para>
/// These members used to sit on <see cref="IDeviceOperationHost"/> itself, which made an SD
/// card — a peripheral only some devices have — part of the seam that <em>every</em> operation
/// collaborator works through. Channel control, device administration, network configuration,
/// diagnostics, the live-sample stream and the frame decoder all had to depend on an interface
/// that talked about SD downloads and about which kind of cable the device was on, and every
/// test double for them had to stub members it would never call.
/// </para>
/// <para>
/// Same arrangement as the seam it extends: every member forwards to a member
/// <see cref="DaqifiStreamingDevice"/> already had, the device implements it explicitly, and the
/// two timeouts are read through the device on each access so a subclass's override of them
/// still applies.
/// </para>
/// </remarks>
internal interface ISdCardOperationHost : IDeviceOperationHost
{
    /// <inheritdoc cref="DaqifiStreamingDevice.IsUsbConnection"/>
    /// <remarks>
    /// The SD operations are the only collaborator that acts on the transport's identity, which
    /// is why it lives on this facet rather than on the shared seam. The card and the WiFi/LAN
    /// module share one SPI bus, so whether the link is USB decides how each SD command prepares
    /// that bus, what a silent transport means to a file transfer, and whether SD logging can be
    /// started at all. Read through the device so a subclass's override of it still applies.
    /// </remarks>
    bool IsUsbConnection { get; }

    /// <inheritdoc cref="DaqifiStreamingDevice.IsLoggingToSdCard"/>
    /// <remarks>
    /// Read from the device's one session value, so it cannot disagree with
    /// <see cref="IDeviceOperationHost.IsStreaming"/>.
    /// <see cref="IDeviceOperationHost.EndStreamingSession"/> is what ends the session (issue #932).
    /// </remarks>
    bool IsLoggingToSdCard { get; }

    /// <summary>
    /// Records that the stream just started is going to the SD card.
    /// </summary>
    void BeginSdCardLogSession();

    /// <summary>
    /// When the streaming rate is one the device object inherited from the device rather than
    /// one anybody chose, lowers it to the most the device says it can stream with the
    /// interface, format and channels now configured, or to Core's default when that cannot be
    /// read. Does nothing for a chosen rate.
    /// </summary>
    /// <remarks>
    /// Called by an SD log start after it has routed the stream to the card and before it sends
    /// the start, because that is the configuration the device will check the rate against
    /// (<see cref="Internal.StreamingSessionController.LowerInheritedFrequency"/>).
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe.</param>
    /// <returns>A task that completes once the rate has been checked.</returns>
    Task LowerInheritedStreamingFrequencyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Overall wall-clock budget for one SD card download, read through the device so a
    /// subclass's override of it still applies.
    /// </summary>
    TimeSpan SdCardDownloadTimeout { get; }

    /// <summary>
    /// Inactivity window for an SD card transfer, read through the device so a subclass's
    /// override of it still applies.
    /// </summary>
    TimeSpan SdCardTransferIdleTimeout { get; }

    /// <summary>
    /// Settle period for a switch of the shared SPI bus between the LAN and the SD card, read
    /// through the device so a subclass's override of it still applies.
    /// </summary>
    /// <remarks>
    /// A test double shortens it to <see cref="TimeSpan.Zero"/> so a test that only cares which
    /// commands went out does not pay two real settle periods per operation. What the delay is
    /// for — that the firmware gets a pause before the next command — is behavior of the
    /// collaborator, and is asserted on the stepped clock rather than by waiting.
    /// </remarks>
    TimeSpan SdInterfaceSettleDelay { get; }

    /// <summary>
    /// The clock the SD operations measure their budgets and settle delays on (issue #637).
    /// </summary>
    /// <remarks>
    /// <see cref="TimeProvider.System"/> on the real device, so every settle delay and every
    /// deadline is what it has always been. It is the seam that makes the two budgets above
    /// testable: <see cref="SdCardDownloadTimeout"/> and
    /// <see cref="SdCardTransferIdleTimeout"/> are counted in seconds, so the watchdog they
    /// arm (issue #399) could previously only be exercised by actually waiting for it — which
    /// is why it was asserted at its short-timeout edges and the real budget went untested.
    /// </remarks>
    TimeProvider TimeProvider { get; }

    /// <summary>
    /// Raises the device's <see cref="DaqifiStreamingDevice.LowSdSpaceWarning"/> event.
    /// </summary>
    /// <remarks>
    /// Deliberately a call back into the device rather than an event the collaborator owns.
    /// The event is part of <see cref="ISdCardOperations"/>, so its <c>sender</c> has to remain
    /// the device a subscriber attached to — a collaborator raising it in its own name would be
    /// a silent, compile-clean behavior change.
    /// </remarks>
    void RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e);

    /// <inheritdoc cref="DaqifiDevice.RunExclusiveAsync(Func{CancellationToken, Task}, CancellationToken)"/>
    /// <remarks>
    /// The SD download needs it on firmware v3.9.0+ (#959). There, the device's SCPI error queue
    /// is read before an <c>SD:GET</c> to empty it, and again after an empty reply to find out
    /// why. A raw capture and each error-queue read take the device lock separately, so without
    /// one block around all three, another caller's command could run between them. A query
    /// could pop the GET's error first, and a failing command could queue one that would then be
    /// blamed on the GET.
    /// </remarks>
    Task RunExclusiveAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="DaqifiDevice.DrainErrorQueueUntilEmptyAsync"/>
    /// <remarks>
    /// The SD download drains the queue before each v3.9.0+ <c>SD:GET</c> as a boundary (#959).
    /// It may blame what it reads afterwards on the GET only when that drain reached
    /// "No error". A drain that stopped early may have left an older <c>-256</c> behind.
    /// </remarks>
    Task<ErrorQueueDrainResult> DrainErrorQueueUntilEmptyAsync(int maxIterations, CancellationToken cancellationToken = default);
}
