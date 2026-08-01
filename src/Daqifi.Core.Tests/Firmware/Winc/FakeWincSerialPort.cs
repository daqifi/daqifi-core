using Daqifi.Core.Firmware.Winc;

namespace Daqifi.Core.Tests.Firmware.Winc;

/// <summary>
/// A fake serial port that emulates the DAQiFi firmware's WINC bridge state machine, so the client
/// can be exercised end-to-end without hardware.
/// </summary>
/// <remarks>
/// This deliberately re-implements the firmware's parser (op-code wait, 12-byte header, XOR
/// validation, ACK/NACK, payload wait) rather than replaying canned bytes. That way a client that
/// frames a command incorrectly gets rejected here exactly as the device would reject it — the
/// point is to catch framing bugs the bench cannot.
/// </remarks>
internal sealed class FakeWincSerialPort : IWincSerialPort
{
    private readonly Queue<byte> _toHost = new();
    private readonly List<byte> _fromHost = [];

    private State _state = State.WaitOpCode;
    private byte[] _header = [];
    private int _pendingPayload;

    /// <summary>Register file the emulated WINC answers reads from.</summary>
    internal Dictionary<uint, uint> Registers { get; } = [];

    /// <summary>Memory the emulated WINC answers block reads from.</summary>
    internal Dictionary<uint, byte[]> Blocks { get; } = [];

    /// <summary>Every command header the host sent, in order.</summary>
    internal List<byte[]> ReceivedHeaders { get; } = [];

    /// <summary>Every block-write payload the host sent, in order.</summary>
    internal List<byte[]> ReceivedPayloads { get; } = [];

    /// <summary>Baud rates the host applied, in order.</summary>
    internal List<int> BaudRateHistory { get; } = [];

    /// <summary>When true, the emulated bridge NACKs the next block write.</summary>
    internal bool FailNextBlockWrite { get; set; }

    /// <summary>When true, the bridge does not answer the identify op code.</summary>
    internal bool SuppressIdentityResponse { get; set; }

    /// <summary>Number of times the host discarded its input buffer.</summary>
    internal int DiscardCount { get; private set; }

    internal bool WasDisposed { get; private set; }

    public bool IsOpen { get; private set; }

    private int _baudRate = 115200;

    public int BaudRate
    {
        get => _baudRate;
        set
        {
            _baudRate = value;
            BaudRateHistory.Add(value);
        }
    }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void DiscardInBuffer()
    {
        DiscardCount++;
        _toHost.Clear();
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Feed(buffer[offset + i]);
        }
    }

    public void ReadExactly(byte[] buffer, int offset, int count, TimeSpan timeout)
    {
        if (_toHost.Count < count)
        {
            throw new TimeoutException(
                $"Fake port has {_toHost.Count} byte(s) buffered but {count} were requested.");
        }

        for (var i = 0; i < count; i++)
        {
            buffer[offset + i] = _toHost.Dequeue();
        }
    }

    public void Dispose()
    {
        WasDisposed = true;
        IsOpen = false;
    }

    /// <summary>Drives the emulated bridge one received byte at a time.</summary>
    private void Feed(byte b)
    {
        _fromHost.Add(b);

        switch (_state)
        {
            case State.WaitOpCode:
                if (b == WincBridgeProtocol.IdentifyVariableBaud)
                {
                    if (!SuppressIdentityResponse)
                    {
                        _toHost.Enqueue(WincBridgeProtocol.Response.IdVariableBaud);
                    }
                }
                else if (b == WincBridgeProtocol.StartCommand)
                {
                    _state = State.WaitHeader;
                    _header = [];
                }
                // Any other op code is ignored, exactly as the firmware does.
                break;

            case State.WaitHeader:
                _header = [.. _header, b];
                if (_header.Length == WincBridgeProtocol.HeaderSize)
                {
                    ReceivedHeaders.Add(_header);
                    HandleHeader();
                }
                break;

            case State.WaitPayload:
                _header = [.. _header, b];
                if (_header.Length == _pendingPayload)
                {
                    ReceivedPayloads.Add(_header);
                    _toHost.Enqueue(
                        FailNextBlockWrite
                            ? WincBridgeProtocol.Response.Nack
                            : WincBridgeProtocol.Response.Ack);
                    FailNextBlockWrite = false;
                    _state = State.WaitOpCode;
                }
                break;
        }
    }

    private void HandleHeader()
    {
        if (!WincBridgeProtocol.IsHeaderValid(_header))
        {
            _toHost.Enqueue(WincBridgeProtocol.Response.Nack);
            _state = State.WaitOpCode;
            return;
        }

        _toHost.Enqueue(WincBridgeProtocol.Response.Ack);

        var command = (WincBridgeProtocol.Command)_header[0];
        var size = (ushort)((_header[3] << 8) | _header[2]);
        var address = ((uint)_header[7] << 24) | ((uint)_header[6] << 16) | ((uint)_header[5] << 8) | _header[4];

        switch (command)
        {
            case WincBridgeProtocol.Command.ReadRegisterWithReturn:
                var value = Registers.TryGetValue(address, out var v) ? v : 0u;
                // Big-endian, matching the firmware.
                _toHost.Enqueue((byte)(value >> 24));
                _toHost.Enqueue((byte)(value >> 16));
                _toHost.Enqueue((byte)(value >> 8));
                _toHost.Enqueue((byte)value);
                _state = State.WaitOpCode;
                break;

            case WincBridgeProtocol.Command.ReadBlock:
                var block = Blocks.TryGetValue(address, out var data) ? data : new byte[size];
                for (var i = 0; i < size; i++)
                {
                    _toHost.Enqueue(i < block.Length ? block[i] : (byte)0);
                }
                _state = State.WaitOpCode;
                break;

            case WincBridgeProtocol.Command.WriteBlock:
                _pendingPayload = size;
                _header = [];
                _state = State.WaitPayload;
                break;

            default:
                // WriteRegister and Reconfigure produce no data beyond the header ACK.
                _state = State.WaitOpCode;
                break;
        }
    }

    private enum State
    {
        WaitOpCode,
        WaitHeader,
        WaitPayload
    }
}
