using Daqifi.Core.Channel;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Unit tests for channel population from protobuf status messages in <see cref="DaqifiDevice"/>.
/// </summary>
public class ChannelPopulationTests
{
    #region PopulateChannelsFromStatus - Basic Tests

    [Fact]
    public void PopulateChannelsFromStatus_WithNullMessage_ThrowsArgumentNullException()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => device.PopulateChannelsFromStatus(null!));
    }

    [Fact]
    public void PopulateChannelsFromStatus_WithEmptyMessage_ReturnsEmptyChannelList()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage();

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.Empty(device.Channels);
    }

    [Fact]
    public void PopulateChannelsFromStatus_RaisesChannelsPopulatedEvent()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            DigitalPortNum = 8
        };

        ChannelsPopulatedEventArgs? eventArgs = null;
        device.ChannelsPopulated += (_, args) => eventArgs = args;

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal(4, eventArgs.AnalogChannelCount);
        Assert.Equal(8, eventArgs.DigitalChannelCount);
        Assert.Equal(12, eventArgs.Channels.Count);
    }

    [Fact]
    public void Channels_ReturnsReadOnlyList()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage { AnalogInPortNum = 2 };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.IsAssignableFrom<IReadOnlyList<IChannel>>(device.Channels);
    }

    #endregion

    #region Analog Channel Population

    [Fact]
    public void PopulateChannelsFromStatus_CreatesCorrectNumberOfAnalogChannels()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 8,
            AnalogInRes = 65535
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var analogChannels = device.Channels.Where(c => c.Type == ChannelType.Analog).ToList();
        Assert.Equal(8, analogChannels.Count);
    }

    [Fact]
    public void PopulateChannelsFromStatus_AnalogChannelsHaveCorrectNames()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            AnalogInRes = 65535
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var analogChannels = device.Channels.Where(c => c.Type == ChannelType.Analog).ToList();
        Assert.Equal("AI0", analogChannels[0].Name);
        Assert.Equal("AI1", analogChannels[1].Name);
        Assert.Equal("AI2", analogChannels[2].Name);
        Assert.Equal("AI3", analogChannels[3].Name);
    }

    [Fact]
    public void PopulateChannelsFromStatus_AnalogChannelsHaveCorrectDirection()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 65535
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var analogChannels = device.Channels.Where(c => c.Type == ChannelType.Analog).ToList();
        Assert.All(analogChannels, c => Assert.Equal(ChannelDirection.Input, c.Direction));
    }

    [Fact]
    public void PopulateChannelsFromStatus_AnalogChannelsAreDisabledByDefault()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 65535
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var analogChannels = device.Channels.Where(c => c.Type == ChannelType.Analog).ToList();
        Assert.All(analogChannels, c => Assert.False(c.IsEnabled));
    }

    [Fact]
    public void PopulateChannelsFromStatus_AnalogChannelsHaveCorrectCalibrationParameters()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 65535
        };
        message.AnalogInCalM.Add(1.5f);
        message.AnalogInCalM.Add(2.0f);
        message.AnalogInCalB.Add(0.1f);
        message.AnalogInCalB.Add(0.2f);
        message.AnalogInIntScaleM.Add(1.1f);
        message.AnalogInIntScaleM.Add(1.2f);
        message.AnalogInPortRange.Add(10.0f);
        message.AnalogInPortRange.Add(5.0f);

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var analogChannels = device.Channels
            .Where(c => c.Type == ChannelType.Analog)
            .Cast<IAnalogChannel>()
            .ToList();

        Assert.Equal(1.5, analogChannels[0].CalibrationM, 3);
        Assert.Equal(2.0, analogChannels[1].CalibrationM, 3);
        Assert.Equal(0.1, analogChannels[0].CalibrationB, 3);
        Assert.Equal(0.2, analogChannels[1].CalibrationB, 3);
        Assert.Equal(1.1, analogChannels[0].InternalScaleM, 3);
        Assert.Equal(1.2, analogChannels[1].InternalScaleM, 3);
        Assert.Equal(10.0, analogChannels[0].PortRange, 3);
        Assert.Equal(5.0, analogChannels[1].PortRange, 3);
    }

    [Fact]
    public void PopulateChannelsFromStatus_AnalogChannelsHaveCorrectResolution()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 4095 // 12-bit resolution
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var analogChannels = device.Channels
            .Where(c => c.Type == ChannelType.Analog)
            .Cast<IAnalogChannel>()
            .ToList();

        Assert.All(analogChannels, c => Assert.Equal(4095u, c.Resolution));
    }

    [Fact]
    public void PopulateChannelsFromStatus_WithZeroResolution_UsesDefaultResolution()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 1,
            AnalogInRes = 0
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var analogChannel = device.Channels
            .Where(c => c.Type == ChannelType.Analog)
            .Cast<IAnalogChannel>()
            .Single();

        Assert.Equal(65535u, analogChannel.Resolution);
    }

    #endregion

    #region Digital Channel Population

    [Fact]
    public void PopulateChannelsFromStatus_CreatesCorrectNumberOfDigitalChannels()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            DigitalPortNum = 16
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var digitalChannels = device.Channels.Where(c => c.Type == ChannelType.Digital).ToList();
        Assert.Equal(16, digitalChannels.Count);
    }

    [Fact]
    public void PopulateChannelsFromStatus_DigitalChannelsHaveCorrectNames()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            DigitalPortNum = 4
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var digitalChannels = device.Channels.Where(c => c.Type == ChannelType.Digital).ToList();
        Assert.Equal("DIO0", digitalChannels[0].Name);
        Assert.Equal("DIO1", digitalChannels[1].Name);
        Assert.Equal("DIO2", digitalChannels[2].Name);
        Assert.Equal("DIO3", digitalChannels[3].Name);
    }

    [Fact]
    public void PopulateChannelsFromStatus_DigitalChannelsHaveCorrectDirection()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            DigitalPortNum = 2
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var digitalChannels = device.Channels.Where(c => c.Type == ChannelType.Digital).ToList();
        Assert.All(digitalChannels, c => Assert.Equal(ChannelDirection.Input, c.Direction));
    }

    [Fact]
    public void PopulateChannelsFromStatus_DigitalChannelsAreEnabledByDefault()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            DigitalPortNum = 2
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var digitalChannels = device.Channels.Where(c => c.Type == ChannelType.Digital).ToList();
        Assert.All(digitalChannels, c => Assert.True(c.IsEnabled));
    }

    #endregion

    #region Mixed Channel Population

    [Fact]
    public void PopulateChannelsFromStatus_CreatesBothAnalogAndDigitalChannels()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 8,
            AnalogInRes = 65535,
            DigitalPortNum = 16
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.Equal(24, device.Channels.Count);
        Assert.Equal(8, device.Channels.Count(c => c.Type == ChannelType.Analog));
        Assert.Equal(16, device.Channels.Count(c => c.Type == ChannelType.Digital));
    }

    [Fact]
    public void PopulateChannelsFromStatus_AnalogChannelsComeBforeDigitalChannels()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            AnalogInRes = 65535,
            DigitalPortNum = 4
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        // First 4 should be analog, last 4 should be digital
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(ChannelType.Analog, device.Channels[i].Type);
        }
        for (int i = 4; i < 8; i++)
        {
            Assert.Equal(ChannelType.Digital, device.Channels[i].Type);
        }
    }

    #endregion

    #region Channel Count Mismatch Handling

    [Fact]
    public void PopulateChannelsFromStatus_WithMissingCalibrationData_UsesDefaults()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            AnalogInRes = 65535
        };
        // Only provide calibration for first 2 channels
        message.AnalogInCalM.Add(1.5f);
        message.AnalogInCalM.Add(2.0f);
        message.AnalogInCalB.Add(0.1f);
        message.AnalogInCalB.Add(0.2f);

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var analogChannels = device.Channels
            .Where(c => c.Type == ChannelType.Analog)
            .Cast<IAnalogChannel>()
            .ToList();

        // First two have provided values
        Assert.Equal(1.5, analogChannels[0].CalibrationM, 3);
        Assert.Equal(0.1, analogChannels[0].CalibrationB, 3);
        Assert.Equal(2.0, analogChannels[1].CalibrationM, 3);
        Assert.Equal(0.2, analogChannels[1].CalibrationB, 3);

        // Last two use defaults (CalM=1.0, CalB=0.0)
        Assert.Equal(1.0, analogChannels[2].CalibrationM, 3);
        Assert.Equal(0.0, analogChannels[2].CalibrationB, 3);
        Assert.Equal(1.0, analogChannels[3].CalibrationM, 3);
        Assert.Equal(0.0, analogChannels[3].CalibrationB, 3);
    }

    [Fact]
    public void PopulateChannelsFromStatus_WithExcessCalibrationData_IgnoresExtra()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            AnalogInRes = 65535
        };
        // Provide calibration for more channels than exist
        message.AnalogInCalM.Add(1.0f);
        message.AnalogInCalM.Add(2.0f);
        message.AnalogInCalM.Add(3.0f); // Extra
        message.AnalogInCalM.Add(4.0f); // Extra

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.Equal(2, device.Channels.Count(c => c.Type == ChannelType.Analog));
    }

    #endregion

    #region Repopulation Tests

    [Fact]
    public void PopulateChannelsFromStatus_ClearsExistingChannelsBeforePopulating()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message1 = new DaqifiOutMessage
        {
            AnalogInPortNum = 8,
            AnalogInRes = 65535,
            DigitalPortNum = 16
        };
        device.PopulateChannelsFromStatus(message1);
        Assert.Equal(24, device.Channels.Count);

        // Act - populate with different configuration
        var message2 = new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            AnalogInRes = 4095,
            DigitalPortNum = 8
        };
        device.PopulateChannelsFromStatus(message2);

        // Assert - should have new configuration, not combined
        Assert.Equal(12, device.Channels.Count);
        Assert.Equal(4, device.Channels.Count(c => c.Type == ChannelType.Analog));
        Assert.Equal(8, device.Channels.Count(c => c.Type == ChannelType.Digital));
    }

    [Fact]
    public void PopulateChannelsFromStatus_MultipleCallsRaiseMultipleEvents()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage { AnalogInPortNum = 2 };
        var eventCount = 0;
        device.ChannelsPopulated += (_, _) => eventCount++;

        // Act
        device.PopulateChannelsFromStatus(message);
        device.PopulateChannelsFromStatus(message);
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.Equal(3, eventCount);
    }

    #endregion

    #region Realistic Device Scenarios

    [Fact]
    public void PopulateChannelsFromStatus_Nyquist1Configuration()
    {
        // Arrange - Simulates a Nyquist 1 device with 8 analog and 16 digital channels
        var device = new DaqifiDevice("Nyquist1");
        var message = new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            AnalogInPortNum = 8,
            AnalogInRes = 65535,
            DigitalPortNum = 16
        };

        // Add realistic calibration data for all 8 channels
        for (int i = 0; i < 8; i++)
        {
            message.AnalogInCalM.Add(1.0f + (i * 0.001f));
            message.AnalogInCalB.Add(0.0f + (i * 0.001f));
            message.AnalogInIntScaleM.Add(1.0f);
            message.AnalogInPortRange.Add(10.0f);
        }

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.Equal(24, device.Channels.Count);

        // Verify analog channels have correct calibration
        var firstAnalog = (IAnalogChannel)device.Channels[0];
        Assert.Equal(1.0, firstAnalog.CalibrationM, 3);
        Assert.Equal(0.0, firstAnalog.CalibrationB, 3);
        Assert.Equal(10.0, firstAnalog.PortRange, 3);
        Assert.Equal(65535u, firstAnalog.Resolution);
    }

    [Fact]
    public void PopulateChannelsFromStatus_Nyquist3Configuration()
    {
        // Arrange - Simulates a Nyquist 3 device with 16 analog and 32 digital channels
        var device = new DaqifiDevice("Nyquist3");
        var message = new DaqifiOutMessage
        {
            DevicePn = "Nq3",
            AnalogInPortNum = 16,
            AnalogInRes = 65535,
            DigitalPortNum = 32
        };

        // Add calibration data
        for (int i = 0; i < 16; i++)
        {
            message.AnalogInCalM.Add(1.0f);
            message.AnalogInCalB.Add(0.0f);
            message.AnalogInIntScaleM.Add(1.0f);
            message.AnalogInPortRange.Add(5.0f);
        }

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.Equal(48, device.Channels.Count);
        Assert.Equal(16, device.Channels.Count(c => c.Type == ChannelType.Analog));
        Assert.Equal(32, device.Channels.Count(c => c.Type == ChannelType.Digital));
    }

    #endregion

    #region ChannelsPopulatedEventArgs Tests

    [Fact]
    public void ChannelsPopulatedEventArgs_ContainsCorrectChannelCounts()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 5,
            AnalogInRes = 65535,
            DigitalPortNum = 10
        };

        ChannelsPopulatedEventArgs? eventArgs = null;
        device.ChannelsPopulated += (_, args) => eventArgs = args;

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal(5, eventArgs.AnalogChannelCount);
        Assert.Equal(10, eventArgs.DigitalChannelCount);
        Assert.Equal(15, eventArgs.Channels.Count);
    }

    [Fact]
    public void ChannelsPopulatedEventArgs_ChannelsMatchDeviceChannels()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 2,
            DigitalPortNum = 3
        };

        ChannelsPopulatedEventArgs? eventArgs = null;
        device.ChannelsPopulated += (_, args) => eventArgs = args;

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.NotNull(eventArgs);
        Assert.Equal(device.Channels.Count, eventArgs.Channels.Count);
        for (int i = 0; i < device.Channels.Count; i++)
        {
            Assert.Same(device.Channels[i], eventArgs.Channels[i]);
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void PopulateChannelsFromStatus_WithOnlyAnalogChannels()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            AnalogInRes = 65535,
            DigitalPortNum = 0
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.Equal(4, device.Channels.Count);
        Assert.All(device.Channels, c => Assert.Equal(ChannelType.Analog, c.Type));
    }

    [Fact]
    public void PopulateChannelsFromStatus_WithOnlyDigitalChannels()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 0,
            DigitalPortNum = 8
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        Assert.Equal(8, device.Channels.Count);
        Assert.All(device.Channels, c => Assert.Equal(ChannelType.Digital, c.Type));
    }

    [Fact]
    public void PopulateChannelsFromStatus_ChannelNumbersAreSequential()
    {
        // Arrange
        var device = new DaqifiDevice("TestDevice");
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 4,
            DigitalPortNum = 4
        };

        // Act
        device.PopulateChannelsFromStatus(message);

        // Assert
        var analogChannels = device.Channels.Where(c => c.Type == ChannelType.Analog).ToList();
        var digitalChannels = device.Channels.Where(c => c.Type == ChannelType.Digital).ToList();

        for (int i = 0; i < analogChannels.Count; i++)
        {
            Assert.Equal(i, analogChannels[i].ChannelNumber);
        }
        for (int i = 0; i < digitalChannels.Count; i++)
        {
            Assert.Equal(i, digitalChannels[i].ChannelNumber);
        }
    }

    #endregion
}
