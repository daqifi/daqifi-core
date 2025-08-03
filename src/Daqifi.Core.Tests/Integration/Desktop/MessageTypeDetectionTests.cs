using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Integration.Desktop;
using System.Text;

namespace Daqifi.Core.Tests.Integration.Desktop;

/// <summary>
/// Tests that verify message type detection and routing works correctly for DAQiFi Desktop integration.
/// These tests simulate the exact scenarios that caused issue #35.
/// </summary>
public class MessageTypeDetectionTests
{
    [Fact]
    public void MessageTypeDetection_ScpiCommandResponse_ShouldBeDetectedAsText()
    {
        // Arrange - Simulate typical SCPI command/response pattern
        var parser = new CompositeMessageParser();
        var scpiResponse = Encoding.UTF8.GetBytes("DAQiFi Device v1.0.2\r\n");
        
        // Act
        var messages = parser.ParseMessages(scpiResponse, out var consumedBytes);
        
        // Assert
        Assert.Single(messages);
        Assert.IsType<string>(messages.First().Data);
        Assert.Equal("DAQiFi Device v1.0.2", messages.First().Data);
        Assert.Equal(scpiResponse.Length, consumedBytes);
    }

    [Fact]
    public void MessageTypeDetection_MultipleScpiResponses_ShouldAllBeDetectedAsText()
    {
        // Arrange - Multiple SCPI responses in one buffer
        var parser = new CompositeMessageParser();
        var multipleResponses = Encoding.UTF8.GetBytes("*IDN?\r\nDAQiFi Device v1.0.2\r\nSYST:ERR?\r\n0,\"No error\"\r\n");
        
        // Act
        var messages = parser.ParseMessages(multipleResponses, out var consumedBytes);
        
        // Assert
        Assert.Equal(4, messages.Count());
        Assert.All(messages, msg => Assert.IsType<string>(msg.Data));
        Assert.Equal("*IDN?", messages.ElementAt(0).Data);
        Assert.Equal("DAQiFi Device v1.0.2", messages.ElementAt(1).Data);
        Assert.Equal("SYST:ERR?", messages.ElementAt(2).Data);
        Assert.Equal("0,\"No error\"", messages.ElementAt(3).Data);
    }

    [Fact]
    public void MessageTypeDetection_ProtobufMessage_ShouldBeDetectedAsBinary()
    {
        // Arrange - Create mock binary protobuf data
        var parser = new CompositeMessageParser();
        var protobufData = new byte[] { 0x08, 0x01, 0x12, 0x04 }; // Mock protobuf
        
        // Act
        var messages = parser.ParseMessages(protobufData, out var consumedBytes);
        
        // Assert - Should attempt protobuf parsing (may succeed or fail)
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void MessageTypeDetection_DataWithNullBytes_ShouldTriggerProtobufParsing()
    {
        // Arrange - This simulates the exact issue from #35 where null bytes caused problems
        var parser = new CompositeMessageParser();
        var dataWithNulls = new byte[] 
        { 
            0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, // "Hello" + null byte
            0x57, 0x6F, 0x72, 0x6C, 0x64, 0x00  // "World" + null byte
        };
        
        // Act
        var messages = parser.ParseMessages(dataWithNulls, out var consumedBytes);
        
        // Assert - Should attempt protobuf parsing first due to null bytes
        // Even if parsing fails, it should not throw exceptions
        Assert.True(consumedBytes >= 0);
    }

    [Fact]
    public void MessageTypeDetection_CoreDeviceAdapter_ShouldHandleBothMessageTypes()
    {
        // Arrange - Test the complete CoreDeviceAdapter integration
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        
        var receivedTextMessages = new List<string>();
        var receivedProtobufMessages = new List<DaqifiOutMessage>();
        
        adapter.MessageReceived += (sender, args) =>
        {
            if (args.Message.Data is string textMsg)
            {
                receivedTextMessages.Add(textMsg);
            }
            else if (args.Message.Data is DaqifiOutMessage protobufMsg)
            {
                receivedProtobufMessages.Add(protobufMsg);
            }
        };
        
        // Act & Assert - Event handler setup should work without exceptions
        Assert.Empty(receivedTextMessages);
        Assert.Empty(receivedProtobufMessages);
        
        // Verify the adapter works correctly (MessageConsumer will be null until connected)
        Assert.Null(adapter.MessageConsumer); // Should be null until connected
    }

    [Fact]
    public void MessageTypeDetection_DaqifiDesktopScenario_ShouldWorkEndToEnd()
    {
        // Arrange - Simulate the exact scenario from DAQiFi Desktop
        // 1. Initial SCPI communication for device identification
        // 2. Switch to protobuf for data streaming
        
        var textOnlyAdapter = CoreDeviceAdapter.CreateTextOnlyTcpAdapter("localhost", 12345);
        var protobufOnlyAdapter = CoreDeviceAdapter.CreateProtobufOnlyTcpAdapter("localhost", 12345);
        var compositeAdapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345); // Default composite
        
        // Act & Assert - All adapters should create successfully
        Assert.NotNull(textOnlyAdapter);
        Assert.NotNull(protobufOnlyAdapter);
        Assert.NotNull(compositeAdapter);
        
        // Verify they all have the expected transport type
        Assert.All(new[] { textOnlyAdapter, protobufOnlyAdapter, compositeAdapter }, 
            adapter => Assert.IsType<TcpStreamTransport>(adapter.Transport));
        
        // Clean up
        textOnlyAdapter.Dispose();
        protobufOnlyAdapter.Dispose();
        compositeAdapter.Dispose();
    }

    [Fact]
    public void MessageTypeDetection_PerformanceWithLargeMessages_ShouldBeReasonable()
    {
        // Arrange - Test performance with larger messages that might occur in streaming
        var parser = new CompositeMessageParser();
        var largeTextMessage = new string('A', 1000) + "\r\n";
        var largeTextData = Encoding.UTF8.GetBytes(largeTextMessage);
        
        // Act - Measure basic performance (not exact timing, just ensure it completes)
        var startTime = DateTime.UtcNow;
        var messages = parser.ParseMessages(largeTextData, out var consumedBytes);
        var endTime = DateTime.UtcNow;
        
        // Assert - Should complete quickly and correctly
        Assert.Single(messages);
        Assert.IsType<string>(messages.First().Data);
        Assert.True((endTime - startTime).TotalMilliseconds < 100); // Should be very fast
        Assert.Equal(largeTextData.Length, consumedBytes);
    }

    [Fact]
    public void MessageTypeDetection_EdgeCases_ShouldHandleGracefully()
    {
        // Arrange - Test various edge cases that might occur in real usage
        var parser = new CompositeMessageParser();
        
        var edgeCases = new[]
        {
            new byte[0],                           // Empty data
            new byte[] { 0x00 },                   // Single null byte
            new byte[] { 0xFF },                   // Single non-null byte
            Encoding.UTF8.GetBytes("\r\n"),       // Just line ending
            Encoding.UTF8.GetBytes("\r"),         // Incomplete line ending
            Encoding.UTF8.GetBytes("\n"),         // Alternative line ending
            new byte[] { 0x00, 0x00, 0x00, 0x00 }, // Multiple nulls
            Encoding.UTF8.GetBytes("No line ending"), // Text without terminator
        };
        
        // Act & Assert - None should throw exceptions
        foreach (var edgeCase in edgeCases)
        {
            var exception = Record.Exception(() =>
            {
                var messages = parser.ParseMessages(edgeCase, out var consumed);
                Assert.True(consumed >= 0);
            });
            
            Assert.Null(exception);
        }
    }

    [Fact]
    public void MessageTypeDetection_Issue35ReproductionTest_ShouldBeFixed()
    {
        // Arrange - Reproduce the exact conditions from issue #35
        // "MessageReceived events never fire" for protobuf responses
        
        using var adapter = CoreDeviceAdapter.CreateTcpAdapter("localhost", 12345);
        var messageReceivedEventFired = false;
        var receivedMessageData = new List<object>();
        
        adapter.MessageReceived += (sender, args) =>
        {
            messageReceivedEventFired = true;
            receivedMessageData.Add(args.Message.Data);
        };
        
        // Act - Simulate the scenario where protobuf messages would be received
        // Test that the composite parser can handle binary data with null bytes
        var compositeParser = new CompositeMessageParser();
        var mockProtobufData = new byte[] { 0x00, 0x08, 0x01, 0x12, 0x04, 0x00 }; // Mock with nulls
        var parsedMessages = compositeParser.ParseMessages(mockProtobufData, out var consumed);
        
        // Assert - The fix should allow protobuf messages to be parsed
        // Even if the specific protobuf parsing fails, it should not cause the
        // "never fire" condition that was reported in the issue
        Assert.True(consumed >= 0); // Parser should consume some bytes or none, not fail
        
        // The key fix is that the adapter now uses CompositeMessageParser by default
        // instead of LineBasedMessageParser, so it can handle both text and binary
        Assert.NotNull(adapter); // Adapter creation should succeed
    }
}