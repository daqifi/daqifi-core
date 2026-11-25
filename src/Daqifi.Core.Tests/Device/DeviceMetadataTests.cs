using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Network;
using Daqifi.Core.Device.Capabilities;
using Google.Protobuf;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Unit tests for the <see cref="DeviceMetadata"/> class.
/// </summary>
public class DeviceMetadataTests
{
    [Fact]
    public void Constructor_InitializesWithDefaultValues()
    {
        // Act
        var metadata = new DeviceMetadata();

        // Assert
        Assert.Equal(string.Empty, metadata.PartNumber);
        Assert.Equal(string.Empty, metadata.SerialNumber);
        Assert.Equal(string.Empty, metadata.FirmwareVersion);
        Assert.Equal(string.Empty, metadata.HardwareRevision);
        Assert.Equal(DeviceType.Unknown, metadata.DeviceType);
        Assert.NotNull(metadata.Capabilities);
        Assert.Equal(string.Empty, metadata.IpAddress);
        Assert.Equal(string.Empty, metadata.MacAddress);
        Assert.Equal(string.Empty, metadata.Ssid);
        Assert.Equal(string.Empty, metadata.HostName);
        Assert.Equal(string.Empty, metadata.FriendlyName);
        Assert.Equal(0, metadata.DevicePort);
    }

    [Fact]
    public void WithStatus_UpdatesPartNumberAndDeviceType()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            DevicePn = "Nq3"
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal("Nq3", metadata.PartNumber);
        Assert.Equal(DeviceType.Nyquist3, metadata.DeviceType);
        Assert.True(metadata.Capabilities.HasSdCard);
        Assert.True(metadata.Capabilities.HasWifi);
    }

    [Fact]
    public void WithStatus_RepeatedPartNumber_DoesNotDiscardChannelCounts()
    {
        // Status messages repeat the part number. Rebuilding the board-derived capabilities on
        // each one reset the channel counts, which a status message carrying no port fields does
        // not restore.
        var metadata = new DeviceMetadata();
        metadata = metadata.WithStatus(new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            AnalogInPortNum = 16,
            DigitalPortNum = 16
        });

        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = "Nq1" });

        Assert.Equal(16, metadata.Capabilities.AnalogInputChannelCount);
        Assert.Equal(16, metadata.Capabilities.DigitalChannelCount);
    }

    [Fact]
    public void WithStatus_ChangedPartNumber_RebuildsBoardDerivedCapabilities()
    {
        // The board really did change (a reconnect against a different unit on the same object),
        // so the board-derived values must be rebuilt rather than carried over.
        var metadata = new DeviceMetadata();
        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = "Nq1", AnalogOutPortNum = 4 });

        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = "Nq3" });

        Assert.Equal(DeviceType.Nyquist3, metadata.DeviceType);
        Assert.Equal(8, metadata.Capabilities.AnalogOutputChannelCount);
    }

    [Fact]
    public void WithCapabilityDocument_OverlaysDocumentAndSurvivesLaterStatusMessages()
    {
        var metadata = new DeviceMetadata();
        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = "Nq1", AnalogInPortNum = 16 });

        Assert.True(CapabilityDocumentParser.TryParse(
            "{\"schema_version\":2,\"streaming\":{\"sample_rate_range_hz\":{\"min\":1,\"max\":22000}}}",
            out var document));
        metadata = metadata.WithCapabilityDocument(document!);

        Assert.Same(document, metadata.CapabilityDocument);
        Assert.Equal(22000, metadata.Capabilities.MaximumSampleRateHz);

        // A later status message must not revert the device's own value to the board table's.
        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = "Nq1", AnalogInPortNum = 16 });

        Assert.Equal(22000, metadata.Capabilities.MaximumSampleRateHz);
        Assert.Equal(16, metadata.Capabilities.AnalogInputChannelCount);
    }

    [Fact]
    public void WithStatus_ChangedPartNumber_DiscardsTheOldBoardsCapabilityDocument()
    {
        // The overlay is durable across status messages by design, but it describes the board it
        // was read from — carrying it onto a different unit connected through the same object
        // would overlay the previous device's flags, channel counts and rate ceiling.
        var metadata = new DeviceMetadata();
        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = "Nq1" });
        Assert.True(CapabilityDocumentParser.TryParse(
            "{\"schema_version\":2,\"streaming\":{\"sample_rate_range_hz\":{\"min\":1,\"max\":22000}},"
            + "\"channels\":[{\"id\":0,\"kind\":\"analog-input\"}]}",
            out var document));
        metadata = metadata.WithCapabilityDocument(document!);
        Assert.Equal(22000, metadata.Capabilities.MaximumSampleRateHz);

        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = "Nq3" });

        Assert.Null(metadata.CapabilityDocument);
        Assert.Equal(16000, metadata.Capabilities.MaximumSampleRateHz);
        Assert.Equal(8, metadata.Capabilities.AnalogInputChannelCount);
    }

    [Theory]
    // Unknown board first (an unrecognized part number), then the real one — a discovery.
    [InlineData("Xx9", "Nq1")]
    // The real board first, then an unrecognized part number — says nothing about a swap.
    [InlineData("Nq1", "Xx9")]
    public void WithStatus_TransitionInvolvingUnknownBoard_KeepsTheCapabilityDocument(
        string firstPartNumber, string secondPartNumber)
    {
        // Per ADR 0001, Unknown means "not yet known", not "a different board". The device that
        // answered the capability query is the same one either way, so discarding the overlay
        // would regress MaximumSampleRateHz to the stale board-table ceiling for no gain.
        var metadata = new DeviceMetadata();
        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = firstPartNumber });
        Assert.True(CapabilityDocumentParser.TryParse(
            "{\"schema_version\":2,\"streaming\":{\"sample_rate_range_hz\":{\"min\":1,\"max\":22000}}}",
            out var document));
        metadata = metadata.WithCapabilityDocument(document!);

        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = secondPartNumber });

        Assert.Same(document, metadata.CapabilityDocument);
        Assert.Equal(22000, metadata.Capabilities.MaximumSampleRateHz);
    }

    [Fact]
    public void WithCapabilityDocument_NullDocument_Throws()
    {
        var metadata = new DeviceMetadata();

        Assert.Throws<ArgumentNullException>(() => metadata.WithCapabilityDocument(null!));
    }

    [Fact]
    public void WithStatus_UpdatesSerialNumber()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            DeviceSn = 12345
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal("12345", metadata.SerialNumber);
    }

    [Fact]
    public void WithStatus_UpdatesFirmwareAndHardwareVersions()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            DeviceFwRev = "1.2.3",
            DeviceHwRev = "2.0"
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal("1.2.3", metadata.FirmwareVersion);
        Assert.Equal("2.0", metadata.HardwareRevision);
    }

    [Fact]
    public void WithStatus_UpdatesNetworkInformation()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            Ssid = "TestNetwork",
            HostName = "daqifi-001",
            DevicePort = 9760,
            WifiSecurityMode = 3,
            WifiInfMode = 1
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal("TestNetwork", metadata.Ssid);
        Assert.Equal("daqifi-001", metadata.HostName);
        Assert.Equal(9760, metadata.DevicePort);
        Assert.Equal(WifiSecurityType.WpaPskPhrase, metadata.WifiSecurityMode);
        Assert.Equal(WifiMode.ExistingNetwork, metadata.WifiInfrastructureMode);
    }

    [Fact]
    public void WithStatus_WpaThenOpenNetwork_AppliesOpenSecurityMode()
    {
        // #986: an open network is security mode 0, which proto3 drops on the wire. Round-trip
        // the SYSInfoPB-shaped status through the encoder so field 60 is genuinely absent.
        var metadata = new DeviceMetadata();
        metadata = metadata.WithStatus(SysInfoStatus(securityMode: 3, infMode: 1));
        Assert.Equal(WifiSecurityType.WpaPskPhrase, metadata.WifiSecurityMode);

        var openStatus = DaqifiOutMessage.Parser.ParseFrom(
            SysInfoStatus(securityMode: 0, infMode: 4).ToByteArray());
        metadata = metadata.WithStatus(openStatus);

        Assert.Equal(WifiSecurityType.None, metadata.WifiSecurityMode);
        Assert.Equal(WifiMode.SelfHosted, metadata.WifiInfrastructureMode);
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("sd-metadata")]
    [InlineData("stream")]
    [InlineData("empty")]
    public void WithStatus_MessageWithoutWifiSettings_KeepsSecurityMode(string kind)
    {
        // Messages that carry no WiFi settings leave field 60 out for a different reason, and
        // must not be read as "the device is now on an open network".
        var metadata = new DeviceMetadata();
        metadata = metadata.WithStatus(SysInfoStatus(securityMode: 3, infMode: 1));

        var message = kind switch
        {
            // The firmware's UDP discovery reply (fields_discovery): identity and ports, no WiFi.
            "discovery" => new DaqifiOutMessage
            {
                DevicePn = "Nq1",
                DeviceFwRev = "3.9.1",
                DeviceHwRev = "2.0",
                DeviceSn = 4242,
                AnalogInPortNum = 16,
                DigitalPortNum = 16,
                HostName = "daqifi-001",
            },
            // The SD log metadata message (fields_sd_metadata).
            "sd-metadata" => new DaqifiOutMessage
            {
                TimestampFreq = 50_000_000,
                AnalogInPortNum = 16,
                DigitalPortNum = 16,
                DeviceSn = 4242,
                DevicePn = "Nq1",
                DeviceFwRev = "3.9.1",
            },
            "stream" => new DaqifiOutMessage { MsgTimeStamp = 1234, AnalogInData = { 1, 2, 3 } },
            _ => new DaqifiOutMessage(),
        };

        metadata = metadata.WithStatus(DaqifiOutMessage.Parser.ParseFrom(message.ToByteArray()));

        Assert.Equal(WifiSecurityType.WpaPskPhrase, metadata.WifiSecurityMode);
        Assert.Equal(WifiMode.ExistingNetwork, metadata.WifiInfrastructureMode);
    }

    [Fact]
    public void WifiSettings_BeforeAnyStatus_AreNull()
    {
        // Not reported must not read as "open network" (#1039).
        var metadata = new DeviceMetadata();

        metadata = metadata.WithStatus(new DaqifiOutMessage { DevicePn = "Nq1", DeviceFwRev = "3.9.1" });

        Assert.Null(metadata.WifiSecurityMode);
        Assert.Null(metadata.WifiInfrastructureMode);
    }

    [Theory]
    [InlineData(0u, WifiSecurityType.None)]
    [InlineData(3u, WifiSecurityType.WpaPskPhrase)]
    [InlineData(4u, WifiSecurityType.WpaPskPhrase)] // WPA_DEPRECATED, the older number for 3
    [InlineData(1u, null)] // WEP_40
    [InlineData(99u, null)]
    public void WithStatus_SecurityMode_MapsKnownValuesAndClearsOnUnknown(uint wireValue, WifiSecurityType? expected)
    {
        // Start from a type the case does not expect, so a kept old value fails.
        var metadata = new DeviceMetadata
        {
            WifiSecurityMode = expected == WifiSecurityType.None ? WifiSecurityType.WpaPskPhrase : WifiSecurityType.None
        };

        metadata = metadata.WithStatus(DaqifiOutMessage.Parser.ParseFrom(
            SysInfoStatus(securityMode: wireValue, infMode: 1).ToByteArray()));

        Assert.Equal(expected, metadata.WifiSecurityMode);
    }

    [Theory]
    [InlineData(1u, WifiMode.ExistingNetwork)]
    [InlineData(4u, WifiMode.SelfHosted)]
    [InlineData(2u, null)]
    [InlineData(99u, null)]
    public void WithStatus_InfrastructureMode_MapsKnownValuesAndClearsOnUnknown(uint wireValue, WifiMode? expected)
    {
        var metadata = new DeviceMetadata
        {
            WifiInfrastructureMode = expected == WifiMode.SelfHosted ? WifiMode.ExistingNetwork : WifiMode.SelfHosted
        };

        metadata = metadata.WithStatus(DaqifiOutMessage.Parser.ParseFrom(
            SysInfoStatus(securityMode: 3, infMode: wireValue).ToByteArray()));

        Assert.Equal(expected, metadata.WifiInfrastructureMode);
    }

    /// <summary>
    /// A status shaped like the firmware's SYSTem:SYSInfoPB? reply (fields_info), which encodes the
    /// WiFi settings block (fields 58-61) together with the device identity.
    /// </summary>
    private static DaqifiOutMessage SysInfoStatus(uint securityMode, uint infMode) => new()
    {
        DevicePn = "Nq1",
        DeviceFwRev = "3.9.1",
        DeviceSn = 4242,
        AnalogInPortNum = 16,
        DigitalPortNum = 16,
        Ssid = "DAQiFi",
        WifiSecurityMode = securityMode,
        WifiInfMode = infMode,
    };

    [Fact]
    public void WithStatus_UpdatesIpAddress()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            IpAddr = ByteString.CopyFrom(new byte[] { 192, 168, 1, 100 })
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal("192.168.1.100", metadata.IpAddress);
    }

    [Fact]
    public void WithStatus_UpdatesMacAddress()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            MacAddr = ByteString.CopyFrom(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF })
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal("AA-BB-CC-DD-EE-FF", metadata.MacAddress);
    }

    [Fact]
    public void WithStatus_UpdatesChannelCounts()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            AnalogInPortNum = 8,
            AnalogOutPortNum = 2,
            DigitalPortNum = 16
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal(8, metadata.Capabilities.AnalogInputChannelCount);
        Assert.Equal(2, metadata.Capabilities.AnalogOutputChannelCount);
        Assert.Equal(16, metadata.Capabilities.DigitalChannelCount);
    }

    [Fact]
    public void WithStatus_FromSerializedStatusPayload_DecodesHealthTelemetry()
    {
        // Arrange: build a status message, serialize it to the wire bytes a device would send,
        // then decode it back through the protobuf parser — exercising the real frame decode path
        // (issue #335 asks for a captured/serialized payload, not just an in-memory message).
        var original = new DaqifiOutMessage
        {
            DevicePn = "Nq1",
            BattStatus = 87,
            TempStatus = -5,
            PwrStatus = 2,
            DeviceStatus = 5
        };
        byte[] payload = original.ToByteArray();
        var decoded = DaqifiOutMessage.Parser.ParseFrom(payload);

        var metadata = new DeviceMetadata();

        // Act
        metadata = metadata.WithStatus(decoded);

        // Assert
        Assert.Equal(87, metadata.Health.BatteryPercent);
        Assert.Equal(-5, metadata.Health.BoardTemperatureCelsius);
        Assert.Equal(2u, metadata.Health.PowerStatus);
        Assert.Equal(5u, metadata.Health.DeviceStatus);
    }

    [Theory]
    [InlineData(101u)]      // above the documented 0-100 range
    [InlineData(500u)]      // clearly nonsensical
    [InlineData(uint.MaxValue)] // would wrap to -1 if cast straight to int
    public void WithStatus_OutOfRangeBattery_IsIgnored(uint battStatus)
    {
        // Arrange: a prior valid reading, then a bad one.
        var metadata = new DeviceMetadata();
        metadata = metadata.WithStatus(new DaqifiOutMessage { BattStatus = 60 });

        // Act
        metadata = metadata.WithStatus(new DaqifiOutMessage { BattStatus = battStatus });

        // Assert: out-of-range battery never surfaces (and never wraps negative); last-known kept.
        Assert.Equal(60, metadata.Health.BatteryPercent);
    }

    [Fact]
    public void WithStatus_NegativeBoardTemperature_IsPreserved()
    {
        // TempStatus is a signed field; sub-zero board temperatures must round-trip.
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage { TempStatus = -10 };

        metadata = metadata.WithStatus(message);

        Assert.Equal(-10, metadata.Health.BoardTemperatureCelsius);
    }

    [Fact]
    public void WithStatus_ZeroHealthFields_LeaveLastKnownValues()
    {
        // A partial status message (all health fields 0) must not clobber a prior reading.
        var metadata = new DeviceMetadata();
        metadata = metadata.WithStatus(new DaqifiOutMessage { BattStatus = 90, TempStatus = 30 });

        metadata = metadata.WithStatus(new DaqifiOutMessage { AnalogInPortNum = 8 });

        Assert.Equal(90, metadata.Health.BatteryPercent);
        Assert.Equal(30, metadata.Health.BoardTemperatureCelsius);
    }

    [Fact]
    public void WithStatus_IgnoresEmptyOrZeroValues()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            DevicePn = "",
            DeviceSn = 0,
            DeviceFwRev = "",
            Ssid = "",
            HostName = "",
            DevicePort = 0
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal(string.Empty, metadata.PartNumber);
        Assert.Equal(string.Empty, metadata.SerialNumber);
        Assert.Equal(string.Empty, metadata.FirmwareVersion);
        Assert.Equal(string.Empty, metadata.Ssid);
        Assert.Equal(string.Empty, metadata.HostName);
        Assert.Equal(0, metadata.DevicePort);
    }

    [Fact]
    public void WithStatus_UpdatesFriendlyName()
    {
        // Arrange
        var metadata = new DeviceMetadata();
        var message = new DaqifiOutMessage
        {
            FriendlyDeviceName = "My Device"
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal("My Device", metadata.FriendlyName);
    }

    [Fact]
    public void WithStatus_WithEmptyFriendlyName_LeavesFriendlyNameUnchanged()
    {
        // Arrange
        var metadata = new DeviceMetadata { FriendlyName = "Previous Name" };
        var message = new DaqifiOutMessage
        {
            FriendlyDeviceName = ""
        };

        // Act
        metadata = metadata.WithStatus(message);

        // Assert
        Assert.Equal("Previous Name", metadata.FriendlyName);
    }

    [Theory]
    [InlineData(typeof(DeviceMetadata))]
    [InlineData(typeof(DeviceCapabilities))]
    [InlineData(typeof(DeviceHealth))]
    public void PublicProperties_CannotBeWrittenOnAnExistingInstance(Type type)
    {
        // #1048: a write to the device's own object changed which features Core allowed.
        var writable = type.GetProperties()
            .Where(property => property.SetMethod is { IsPublic: true } setter
                && !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
            .Select(property => property.Name);

        Assert.Empty(writable);
    }

    [Fact]
    public void WithStatus_LeavesTheOriginalSnapshotAlone()
    {
        var before = new DeviceMetadata().WithStatus(new DaqifiOutMessage { DevicePn = "Nq1", BattStatus = 80 });

        var after = before.WithStatus(new DaqifiOutMessage
        {
            DevicePn = "Nq3",
            DeviceFwRev = "3.9.1",
            AnalogInPortNum = 4,
            BattStatus = 20
        });

        Assert.Equal("Nq1", before.PartNumber);
        Assert.Equal(string.Empty, before.FirmwareVersion);
        Assert.Equal(16, before.Capabilities.AnalogInputChannelCount);
        Assert.Equal(80, before.Health.BatteryPercent);
        Assert.Equal(4, after.Capabilities.AnalogInputChannelCount);
        Assert.Equal(20, after.Health.BatteryPercent);
    }

    [Fact]
    public void With_OnAnExternallyBuiltInstance_ChangesOnlyTheNamedField()
    {
        // How a wrapper that keeps its own copy replaces a property write.
        var metadata = new DeviceMetadata { SerialNumber = "1", FirmwareVersion = "3.8.0" };

        var moved = metadata with { IpAddress = "192.168.1.5" };

        Assert.Equal(string.Empty, metadata.IpAddress);
        Assert.Equal(metadata, moved with { IpAddress = string.Empty });
    }

    [Fact]
    public void WithStatus_FirstStatusOfAnUnrecognizedBoard_KeepsEarlierChannelCounts()
    {
        var metadata = new DeviceMetadata()
            .WithStatus(new DaqifiOutMessage { AnalogInPortNum = 8 })
            .WithStatus(new DaqifiOutMessage { DevicePn = "Xx9" });

        Assert.Equal(DeviceType.Unknown, metadata.DeviceType);
        Assert.Equal(8, metadata.Capabilities.AnalogInputChannelCount);
    }
}
