using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using System.Collections.Generic;
using Xunit;

namespace Daqifi.Core.Tests.Device;

public class DaqifiStreamingDeviceTests
{
    [Fact]
    public void Constructor_InitializesSampleRateHz()
    {
        // Arrange & Act
        var device = new TransportlessStreamingDevice("TestDevice");

        // Assert
        Assert.Equal(100, device.SampleRateHz);
        Assert.False(device.IsStreaming);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(1000)] // MaximumSampleRateHz for all Nyquist types
    public void SampleRateHz_WithinRange_IsAccepted(int frequency)
    {
        // Arrange
        var device = new TransportlessStreamingDevice("TestDevice");

        // Act
        device.SampleRateHz = frequency;

        // Assert
        Assert.Equal(frequency, device.SampleRateHz);
    }

    [Theory]
    [InlineData(1001)] // MaximumSampleRateHz + 1
    [InlineData(50000)]
    public void SampleRateHz_AboveTheCeiling_ThrowsAndQuotesTheCeiling(int frequency)
    {
        // Arrange
        var device = new TransportlessStreamingDevice("TestDevice");

        // Act & Assert
        var exception = Assert.Throws<System.ArgumentOutOfRangeException>(() => device.SampleRateHz = frequency);
        Assert.Contains("1000", exception.Message); // the ceiling, surfaced from DeviceCapabilities.MaximumSampleRateHz
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SampleRateHz_BelowOne_ThrowsAndQuotesOnlyTheLowerBound(int frequency)
    {
        // #899: a value under 1 is refused by the lower bound, so the message quotes that and
        // nothing else. It used to report "must be between 1 and <ceiling> Hz (the device's
        // maximum sampling rate)", which named a ceiling the value never approached — and named
        // it as if it were the limit a stream is held to, which it is not.
        var device = new TransportlessStreamingDevice("TestDevice");

        var exception = Assert.Throws<System.ArgumentOutOfRangeException>(() => device.SampleRateHz = frequency);

        Assert.Contains("at least 1 Hz", exception.Message);
        Assert.DoesNotContain("1000", exception.Message);
    }

    [Fact]
    public void SampleRateHz_OutOfRange_LeavesPreviousValueUnchanged()
    {
        // Arrange
        var device = new TransportlessStreamingDevice("TestDevice") { SampleRateHz = 250 };

        // Act
        Assert.Throws<System.ArgumentOutOfRangeException>(() => device.SampleRateHz = 5000);

        // Assert
        Assert.Equal(250, device.SampleRateHz);
    }

    [Fact]
    public void SampleRateHz_UsesCapabilitiesMaximumSampleRateHz_NotAHardcodedConstant()
    {
        // Arrange: lower the device's advertised max and confirm the guard tracks it.
        var device = new TransportlessStreamingDevice("TestDevice");
        device.UpdateMetadata(metadata => metadata with
        {
            Capabilities = metadata.Capabilities with { MaximumSampleRateHz = 200 }
        });

        // Act & Assert: 200 is now the ceiling, 201 is rejected.
        device.SampleRateHz = 200;
        Assert.Equal(200, device.SampleRateHz);
        Assert.Throws<System.ArgumentOutOfRangeException>(() => device.SampleRateHz = 201);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void SampleRateHz_InvalidCapabilitiesMax_SanitizesCeilingToOne(int invalidMax)
    {
        // Arrange: MaximumSampleRateHz is unvalidated. An invalid value from a subclass
        // must not produce an impossible range that rejects every frequency.
        var device = new TransportlessStreamingDevice("TestDevice");
        device.UpdateMetadata(metadata => metadata with
        {
            Capabilities = metadata.Capabilities with { MaximumSampleRateHz = invalidMax }
        });

        // Act & Assert: the ceiling is sanitized to 1, so 1 is accepted and 2 is rejected.
        device.SampleRateHz = 1;
        Assert.Equal(1, device.SampleRateHz);

        var ex = Assert.Throws<System.ArgumentOutOfRangeException>(() => device.SampleRateHz = 2);
        Assert.Contains("at most 1 Hz", ex.Message); // the sanitized ceiling, not an impossible "1..0"
    }

    [Fact]
    public void StartStreaming_WhenConnected_SendsCorrectCommandAndSetsIsStreaming()
    {
        // Arrange
        var device = new TestableDaqifiStreamingDevice("TestDevice");
        device.Connect();
        device.SampleRateHz = 200;

        // Act
        device.StartStreaming();

        // Assert
        Assert.True(device.IsStreaming);
        var sentMessage = Assert.Single(device.SentMessages);
        var expectedMessage = ScpiMessageProducer.StartStreaming(200);
        Assert.Equal(expectedMessage.Data, sentMessage.Data);
    }

    [Fact]
    public void StopStreaming_WhenConnected_SendsCorrectCommandAndSetsIsStreaming()
    {
        // Arrange
        var device = new TestableDaqifiStreamingDevice("TestDevice");
        device.Connect();
        device.StartStreaming();
        device.SentMessages.Clear();

        // Act
        device.StopStreaming();

        // Assert
        Assert.False(device.IsStreaming);
        var sentMessage = Assert.Single(device.SentMessages);
        var expectedMessage = ScpiMessageProducer.StopStreaming;
        Assert.Equal(expectedMessage.Data, sentMessage.Data);
    }

    /// <summary>
    /// A testable version of DaqifiStreamingDevice that captures sent messages.
    /// </summary>
    private class TestableDaqifiStreamingDevice : DaqifiStreamingDevice
    {
        public List<IOutboundMessage<string>> SentMessages { get; } = new();

        public TestableDaqifiStreamingDevice(string name) : base(name)
        {
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            // Override to capture the message instead of sending it.
            // This avoids the base class's check for a real connection.
            if (message is IOutboundMessage<string> stringMessage)
            {
                SentMessages.Add(stringMessage);
            }
        }
    }
}
