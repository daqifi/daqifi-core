using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// Decides whether a device being initialized must have its stream re-routed to USB, and how
/// hard to try, given only what the caller reports about the connection.
/// </summary>
/// <remarks>
/// <para>
/// The DAQiFi firmware persists the last configured stream interface across sessions. If the
/// device was previously set to stream to WiFi (<c>SYSTem:STReam:INTerface 1</c>), it keeps
/// sending data over WiFi even when connected via USB — so the serial consumer receives nothing
/// until <c>SYSTem:STReam:INTerface 0</c> is sent.
/// </para>
/// <para>
/// This type owns the decision (route or skip, retry or fail) and nothing else. Sending the
/// command is the caller's effect, supplied as a delegate, so the policy is testable without a
/// device, a transport, or a wire.
/// </para>
/// </remarks>
internal static class UsbStreamInterfaceInitializer
{
    /// <summary>
    /// Maximum number of retry attempts for the USB stream-interface command when the device
    /// returns a transient SCPI error (e.g. because the firmware still has the interface set
    /// from a prior WiFi session).
    /// </summary>
    internal const int MaxRetries = 1;

    /// <summary>
    /// Delay in milliseconds before retrying the USB stream-interface command after a transient
    /// SCPI error.
    /// </summary>
    internal const int RetryDelayMs = 150;

    /// <summary>
    /// Decides whether the stream-routing command should be sent at all.
    /// </summary>
    /// <remarks>
    /// The routing command is global device state: it takes the stream away from whatever
    /// interface it was going to, so a second session running it steals another session's data
    /// (#385). An observe-only session must therefore skip it entirely, and a non-USB connection
    /// has nothing to route.
    /// </remarks>
    /// <param name="isUsbConnection">Whether the device is connected over USB/serial.</param>
    /// <param name="preserveActiveStream">
    /// Whether this initialization must leave a stream another session is already running
    /// untouched.
    /// </param>
    /// <returns><c>true</c> when the command should be sent; otherwise <c>false</c>.</returns>
    internal static bool ShouldRouteStreamToUsb(bool isUsbConnection, bool preserveActiveStream)
        => isUsbConnection && !preserveActiveStream;

    /// <summary>
    /// Routes the device's stream to USB, retrying a transient SCPI rejection before treating it
    /// as a hard failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see cref="ShouldRouteStreamToUsb"/> says to skip, this returns without invoking
    /// <paramref name="setStreamInterfaceToUsbAsync"/> and without observing
    /// <paramref name="cancellationToken"/>. Not observing it is deliberate: there is no work to
    /// abandon, and the caller's initialization re-checks cancellation before it marks the device
    /// ready, so a token cancelled during this step is still honored.
    /// </para>
    /// <para>
    /// The firmware can transiently reject the command with a <c>-200 "Execution error"</c> right
    /// after connect, so a rejection is retried once after a settle delay (mirrors the SD card
    /// retry). Only a rejection that persists across every attempt is a failure.
    /// </para>
    /// </remarks>
    /// <param name="isUsbConnection">Whether the device is connected over USB/serial.</param>
    /// <param name="preserveActiveStream">
    /// Whether this initialization must leave a stream another session is already running
    /// untouched.
    /// </param>
    /// <param name="setStreamInterfaceToUsbAsync">
    /// Sends the routing command and returns the device's response lines. Invoked once per
    /// attempt.
    /// </param>
    /// <param name="cancellationToken">A cancellation token to observe while routing.</param>
    /// <returns>A task representing the asynchronous routing operation.</returns>
    /// <exception cref="ScpiInitializationErrorException">
    /// Thrown when the device returns a SCPI error on every attempt.
    /// </exception>
    internal static async Task RouteStreamToUsbAsync(
        bool isUsbConnection,
        bool preserveActiveStream,
        Func<CancellationToken, Task<IReadOnlyList<string>>> setStreamInterfaceToUsbAsync,
        CancellationToken cancellationToken)
    {
        if (!ShouldRouteStreamToUsb(isUsbConnection, preserveActiveStream))
        {
            return;
        }

        IReadOnlyList<string> lines = Array.Empty<string>();
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
            }

            lines = await setStreamInterfaceToUsbAsync(cancellationToken).ConfigureAwait(false);

            if (!ScpiResponseClassifier.ContainsScpiError(lines))
            {
                return;
            }
        }

        var lastScpiError = ScpiResponseClassifier.GetLastScpiErrorLine(lines);
        throw new ScpiInitializationErrorException(
            "Device returned a SCPI error while setting stream interface to USB.",
            lines,
            lastScpiError);
    }
}
