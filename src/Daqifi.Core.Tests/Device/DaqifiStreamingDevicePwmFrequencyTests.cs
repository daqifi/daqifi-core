using System.Collections.Generic;
using System.Linq;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class DaqifiStreamingDevicePwmFrequencyTests
    {
        [Fact]
        public void SetPwmFrequency_FirstCall_SendsAndTracksFrequency()
        {
            var device = new CapturingStreamingDevice();
            device.Connect();

            device.SetPwmFrequency(2000);

            Assert.Equal(new[] { "PWM:CHannel:FREQuency 0,2000" }, device.PwmFrequencySends);
            Assert.Equal(2000, device.PwmFrequencyHz);
        }

        [Fact]
        public void SetPwmFrequency_SameValueTwice_SecondIsSkipped()
        {
            var device = new CapturingStreamingDevice();
            device.Connect();

            device.SetPwmFrequency(2000);
            device.SetPwmFrequency(2000); // unchanged -> no second send

            Assert.Single(device.PwmFrequencySends);
            Assert.Equal(2000, device.PwmFrequencyHz);
        }

        [Fact]
        public void SetPwmFrequency_ChangedValue_Sends()
        {
            var device = new CapturingStreamingDevice();
            device.Connect();

            device.SetPwmFrequency(2000);
            device.SetPwmFrequency(2000); // skipped
            device.SetPwmFrequency(3000); // changed -> sends
            device.SetPwmFrequency(3000); // skipped

            Assert.Equal(
                new[] { "PWM:CHannel:FREQuency 0,2000", "PWM:CHannel:FREQuency 0,3000" },
                device.PwmFrequencySends);
            Assert.Equal(3000, device.PwmFrequencyHz);
        }

        [Fact]
        public void SetPwmFrequency_AfterReconnect_SendsAgainEvenIfUnchanged()
        {
            var device = new CapturingStreamingDevice();

            device.Connect();
            device.SetPwmFrequency(2000);
            device.Disconnect(); // clears the "already-sent" cache

            device.Connect();
            device.SetPwmFrequency(2000); // fresh connection must re-send

            Assert.Equal(
                new[] { "PWM:CHannel:FREQuency 0,2000", "PWM:CHannel:FREQuency 0,2000" },
                device.PwmFrequencySends);
        }

        [Fact]
        public void SetPwmFrequency_NotConnected_ThrowsAndSendsNothing()
        {
            var device = new CapturingStreamingDevice(); // not connected
            Assert.Throws<System.InvalidOperationException>(() => device.SetPwmFrequency(2000));
            Assert.Empty(device.PwmFrequencySends);
        }

        [Theory]
        [InlineData(5)]      // below MinPwmFrequencyHz (6)
        [InlineData(50_001)] // above MaxPwmFrequencyHz (50000)
        public void SetPwmFrequency_OutOfRange_ThrowsBeforeConnectedCheck(int hz)
        {
            var device = new CapturingStreamingDevice();
            device.Connect();
            Assert.Throws<System.ArgumentOutOfRangeException>(() => device.SetPwmFrequency(hz));
            Assert.Empty(device.PwmFrequencySends);
        }

        private sealed class CapturingStreamingDevice : DaqifiStreamingDevice
        {
            public CapturingStreamingDevice() : base("TestDevice") { }

            private readonly List<string> _sent = new();

            /// <summary>The PWM-frequency SCPI commands actually sent (skips are absent).</summary>
            public IReadOnlyList<string> PwmFrequencySends =>
                _sent.Where(s => s.StartsWith("PWM:CHannel:FREQuency")).ToList();

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    _sent.Add(stringMessage.Data);
                }
            }
        }
    }
}
