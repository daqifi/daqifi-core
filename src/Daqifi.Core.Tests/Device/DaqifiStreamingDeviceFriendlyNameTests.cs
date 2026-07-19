using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class DaqifiStreamingDeviceFriendlyNameTests
    {
        [Fact]
        public async Task SetFriendlyNameAsync_NullName_Throws()
        {
            var device = new CapturingStreamingDevice();
            device.Connect();
            await Assert.ThrowsAsync<ArgumentNullException>(() => device.SetFriendlyNameAsync(null!));
        }

        [Theory]
        [InlineData("")]                                     // too short
        [InlineData("this-name-is-far-too-long-to-be-valid")] // > 31 chars
        [InlineData("bad\"quote")]                            // contains "
        [InlineData("bad\\slash")]                            // contains \
        public async Task SetFriendlyNameAsync_InvalidName_Throws(string name)
        {
            var device = new CapturingStreamingDevice();
            device.Connect();
            await Assert.ThrowsAsync<ArgumentException>(() => device.SetFriendlyNameAsync(name));
            Assert.Empty(device.Sent); // nothing sent for an invalid name
        }

        [Fact]
        public async Task SetFriendlyNameAsync_NotConnected_Throws()
        {
            var device = new CapturingStreamingDevice(); // not connected
            await Assert.ThrowsAsync<InvalidOperationException>(() => device.SetFriendlyNameAsync("Lab Nq1"));
        }

        [Fact]
        public async Task SetFriendlyNameAsync_ValidName_SendsSetThenSave_AndUpdatesMetadata()
        {
            var device = new CapturingStreamingDevice();
            device.Connect();

            await device.SetFriendlyNameAsync("Lab Nq1");

            Assert.Equal(
                new[] { "SYSTem:DEVice:NAME \"Lab Nq1\"", "SYSTem:DEVice:NAME:SAVE" },
                device.Sent);
            Assert.Equal("Lab Nq1", device.Metadata.FriendlyName); // optimistic local update
        }

        [Fact]
        public async Task SetFriendlyNameAsync_Cancelled_ThrowsAndSendsNothing()
        {
            var device = new CapturingStreamingDevice();
            device.Connect();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => device.SetFriendlyNameAsync("Lab Nq1", cts.Token));
            Assert.Empty(device.Sent);
        }

        [Fact]
        public async Task SetFriendlyNameAsync_MaxLengthName_IsAccepted()
        {
            var device = new CapturingStreamingDevice();
            device.Connect();
            var name = new string('a', 31); // exactly MaxFriendlyNameLength

            await device.SetFriendlyNameAsync(name);

            Assert.Equal(name, device.Metadata.FriendlyName);
            Assert.Equal(2, device.Sent.Count);
        }

        private sealed class CapturingStreamingDevice : DaqifiStreamingDevice
        {
            public CapturingStreamingDevice() : base("TestDevice") { }

            public List<string> Sent { get; } = new();

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    Sent.Add(stringMessage.Data);
                }
            }
        }
    }
}
