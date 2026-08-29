using Daqifi.Core.Device.Internal;
using System;

#nullable enable

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
}
