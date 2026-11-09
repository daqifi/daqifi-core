using System;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// One text exchange's hold on the device's reader. While it is installed on the
/// <see cref="DeviceWireParser"/>, the reader frames what arrives as lines instead of protobuf
/// frames, and every line it frames is tagged with this capture and handed back to the exchange
/// that opened it (issue #900).
/// </summary>
/// <remarks>
/// <para>
/// A line carries its capture rather than being delivered to "whichever exchange is current"
/// because the reader frames a batch under its buffer lock and dispatches it afterwards, outside
/// that lock. A line framed in the last instant of one exchange can therefore be dispatched after
/// the next exchange has opened. Tagged with the capture it was framed under, it can only ever
/// reach the exchange it belongs to — and that one has finished listening, so it is dropped.
/// </para>
/// <para>
/// Closing is one-way and cheap. A line that is dispatched after <see cref="Close"/> is dropped;
/// one dispatched in the same instant as the close may still be delivered, which is the same race
/// a line arriving at the end of any collection window has always had, and the exchange's own gate
/// is what keeps it safe.
/// </para>
/// </remarks>
internal sealed class TextReplyCapture
{
    private readonly Action<string> _onLine;
    private volatile bool _closed;

    /// <summary>
    /// Creates a capture that hands each line it is given to <paramref name="onLine"/>.
    /// </summary>
    /// <param name="onLine">
    /// Called on the reader thread, outside the reader's buffer lock, once per line — blank lines
    /// included. It must stay cheap and must not block: the reader dispatches nothing else while
    /// it runs.
    /// </param>
    internal TextReplyCapture(Action<string> onLine)
    {
        _onLine = onLine ?? throw new ArgumentNullException(nameof(onLine));
    }

    /// <summary>
    /// True once the exchange that opened this capture has stopped listening.
    /// </summary>
    internal bool IsClosed => _closed;

    /// <summary>
    /// Hands one framed line to the exchange, unless it has already stopped listening.
    /// </summary>
    /// <param name="line">The line, trimmed, possibly empty.</param>
    internal void Deliver(string line)
    {
        if (_closed)
        {
            return;
        }

        _onLine(line);
    }

    /// <summary>
    /// Stops delivering lines. Idempotent.
    /// </summary>
    internal void Close() => _closed = true;
}
