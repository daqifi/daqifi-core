using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Daqifi.Core.Tests.Device;

public class DaqifiDeviceWithMessageProducerTests
{
    private sealed class BinaryOutboundMessage : IOutboundMessage<byte[]>
    {
        public byte[] Data { get; set; }

        public BinaryOutboundMessage(byte[] data)
        {
            Data = data;
        }

        public byte[] GetBytes() => Data;
    }

    /// <summary>
    /// A write-only stream whose <see cref="Write"/> always throws, used to exercise the
    /// device's wiring of <see cref="IMessageProducer{T}.SendFailed"/> (issue #408).
    /// </summary>
    private sealed class ThrowOnWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Simulated write failure for error-handling test.");
    }

    /// <summary>
    /// Captures log entries in-memory so tests can assert that the device logs as expected.
    /// </summary>
    private sealed class CaptureLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, Exception? Exception, string Message)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, exception, formatter(state, exception)));
    }

    [Fact]
    public void DaqifiDevice_WithStream_ShouldInitializeMessageProducer()
    {
        // Arrange
        using var stream = new MemoryStream();
        
        // Act
        using var device = new DaqifiDevice("Test Device", stream, IPAddress.Loopback);
        
        // Assert
        Assert.Equal("Test Device", device.Name);
        Assert.Equal(IPAddress.Loopback, device.IpAddress);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public void DaqifiDevice_Connect_ShouldStartMessageProducer()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        
        // Act
        device.Connect();
        
        // Assert
        Assert.True(device.IsConnected);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
    }

    [Fact]
    public void DaqifiDevice_SendMessage_WhenConnected_ShouldWriteToStream()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        device.Connect();
        
        // Act
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        
        // Wait for background thread to process the message
        Thread.Sleep(200);
        
        // Assert
        var written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("SYSTem:SYSInfoPB?", written);
    }

    [Fact]
    public void DaqifiDevice_SendMessage_WhenDisconnected_ShouldThrowException()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        
        // Act & Assert
        Assert.Throws<DeviceNotConnectedException>(() => device.Send(ScpiMessageProducer.GetDeviceInfo));
    }

    [Fact]
    public void DaqifiDevice_SendNonStringMessage_WhenConnected_WritesDirectlyToStream()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        device.Connect();
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        // Act
        device.Send(new BinaryOutboundMessage(payload));

        // Assert - non-string payloads bypass the queued producer and are written synchronously.
        Assert.Equal(payload, stream.ToArray());
    }

    [Fact]
    public void DaqifiDevice_ProducerlessConstructor_SendAnyMessage_ThrowsInvalidOperationException()
    {
        // Arrange - the (name, ipAddress) constructor has no transport or stream to send on.
        using var device = new DaqifiDevice("Test Device", IPAddress.Loopback);
        device.Connect();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => device.Send(ScpiMessageProducer.GetDeviceInfo));
        Assert.Contains("no transport or stream", ex.Message);
    }

    [Fact]
    public void DaqifiDevice_SendMessage_WhenWriteFails_ShouldLogWarning()
    {
        // Arrange - a command that never reaches the device must not look delivered (issue #408);
        // the device wires the producer's SendFailed event to its own logger so the failure is
        // visible even though Send() itself never throws.
        using var stream = new ThrowOnWriteStream();
        var logger = new CaptureLogger();
        using var device = new DaqifiDevice("Test Device", stream, logger: logger);
        device.Connect();

        // Act
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        var warningLogged = SpinWait.SpinUntil(
            () => logger.Entries.Any(e => e.Level == LogLevel.Warning),
            TimeSpan.FromSeconds(2));

        // Assert
        Assert.True(warningLogged, "Expected a warning to be logged when a queued message fails to write.");
        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning);
        Assert.IsType<IOException>(warning.Exception);
    }

    [Fact]
    public void DaqifiDevice_SendMessage_WhenWriteFails_ShouldRaiseSendFailed()
    {
        // Arrange - DaqifiDevice.SendFailed is the only way a caller can react to a dropped
        // command, since Send() itself never throws for a failed write (issue #408).
        using var stream = new ThrowOnWriteStream();
        using var device = new DaqifiDevice("Test Device", stream);
        device.Connect();
        MessageSendFailedEventArgs<string>? captured = null;
        device.SendFailed += (_, e) => captured = e;

        // Act
        device.Send(ScpiMessageProducer.GetDeviceInfo);
        var raised = SpinWait.SpinUntil(() => captured != null, TimeSpan.FromSeconds(2));

        // Assert
        Assert.True(raised, "Expected DaqifiDevice.SendFailed to be raised when a queued message fails to write.");
        Assert.IsType<IOException>(captured!.Error);
    }

    [Fact]
    public void DaqifiDevice_Disconnect_ShouldStopMessageProducer()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        device.Connect();
        
        // Act
        device.Disconnect();
        
        // Assert
        Assert.False(device.IsConnected);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
    }

    [Fact]
    public void DaqifiDevice_StatusChanged_ShouldFireEvent()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var device = new DaqifiDevice("Test Device", stream);
        ConnectionStatus? capturedStatus = null;
        
        device.StatusChanged += (sender, args) => capturedStatus = args.Status;
        
        // Act
        device.Connect();
        
        // Assert
        Assert.Equal(ConnectionStatus.Connected, capturedStatus);
    }
}