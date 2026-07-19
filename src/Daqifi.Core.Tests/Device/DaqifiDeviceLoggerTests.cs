using System;
using System.Collections.Generic;
using System.Linq;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class DaqifiDeviceLoggerTests
    {
        [Fact]
        public void PopulateChannels_NoUsableResolution_LogsWarningThroughInjectedLogger()
        {
            var logger = new CapturingLogger();
            var device = new DaqifiStreamingDevice("Lab Nq1", ipAddress: null, logger: logger);
            device.Connect();

            device.PopulateChannelsFromStatus(StatusWithResolution(analogCount: 2, resolution: 0));

            var warning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning);
            Assert.NotEqual(default, warning);
            Assert.Contains("no usable ADC resolution", warning.Message);
            Assert.Contains("Lab Nq1", warning.Message); // device name rendered from the template
        }

        [Fact]
        public void PopulateChannels_ValidStatus_LogsNoWarning()
        {
            var logger = new CapturingLogger();
            var device = new DaqifiStreamingDevice("Lab Nq1", ipAddress: null, logger: logger);
            device.Connect();

            device.PopulateChannelsFromStatus(StatusWithResolution(analogCount: 2, resolution: 65535));

            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        }

        [Fact]
        public void PopulateChannels_NoLogger_UsesNullLogger_DoesNotThrow()
        {
            // No logger supplied — the device must fall back to a no-op logger, not NRE on a warning path.
            var device = new DaqifiStreamingDevice("Lab Nq1"); // logger defaults to null -> NullLogger
            device.Connect();

            var ex = Record.Exception(() => device.PopulateChannelsFromStatus(StatusWithResolution(2, resolution: 0)));
            Assert.Null(ex);
        }

        [Fact]
        public void DeviceConnectionOptions_Logger_DefaultsToNull()
        {
            Assert.Null(new DeviceConnectionOptions().Logger);
        }

        [Fact]
        public void PopulateChannels_ThrowingLogger_IsSwallowed_DoesNotPropagate()
        {
            // A misbehaving consumer logger must never take down device operation (SafeLog isolation).
            var device = new DaqifiStreamingDevice("Lab Nq1", ipAddress: null, logger: new ThrowingLogger());
            device.Connect();

            var ex = Record.Exception(() => device.PopulateChannelsFromStatus(StatusWithResolution(2, resolution: 0)));
            Assert.Null(ex);
        }

        private static DaqifiOutMessage StatusWithResolution(int analogCount, uint resolution)
        {
            var status = new DaqifiOutMessage
            {
                AnalogInPortNum = (uint)analogCount,
                DigitalPortNum = 0,
                AnalogInRes = resolution,
            };
            for (var i = 0; i < analogCount; i++) status.AnalogInPortRange.Add(1.0f);
            return status;
        }

        private sealed class CapturingLogger : ILogger
        {
            public readonly List<(LogLevel Level, string Message)> Entries = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => throw new InvalidOperationException("boom");
        }
    }
}
