using System.Diagnostics;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using DeviceType = Daqifi.Core.Device.DeviceType;

namespace Daqifi.Core.SmokeTest;

/// <summary>
/// Hardware-in-the-loop smoke test for a USB-connected DAQiFi device.
///
/// Designed for a developer-local sanity check after touching the SDK:
/// discover → connect → read metadata → enable channels → stream briefly
/// → stop → disconnect. Read-only with respect to persistent device state
/// (no SD card writes, no network config changes, no firmware updates).
///
/// Exits with a named, stable code so a wrapping skill or CI step can
/// react to the failure mode rather than parsing the message text.
/// </summary>
internal static class Program
{
    private const int DefaultBaudRate = 9600;
    private const int DefaultStreamRateHz = 100;
    private const int DefaultStreamDurationSeconds = 2;
    private const string DefaultChannelBitmask = "3";
    private const int DiscoveryTimeoutSeconds = 6;
    private const int ConnectAttemptCount = 2;

    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            PrintUsage();
            return (int)ExitCode.BadArgs;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return (int)ExitCode.Success;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            return (int)await RunAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL  unexpected error: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return (int)ExitCode.Unexpected;
        }
        finally
        {
            stopwatch.Stop();
            Console.WriteLine($"Total elapsed: {stopwatch.Elapsed.TotalSeconds:0.00}s");
        }
    }

    private static async Task<ExitCode> RunAsync(Options options)
    {
        // ─── Step 1: locate the device ──────────────────────────────────────
        IDeviceInfo? deviceInfo;
        if (options.PortName == "auto")
        {
            Console.WriteLine($"Step 1/6  Discovering DAQiFi USB devices (timeout {DiscoveryTimeoutSeconds}s)…");
            using var finder = new SerialDeviceFinder(options.BaudRate);
            var discovered = (await finder.DiscoverAsync(TimeSpan.FromSeconds(DiscoveryTimeoutSeconds))
                .ConfigureAwait(false)).ToList();

            if (discovered.Count == 0)
            {
                Console.Error.WriteLine("FAIL  no DAQiFi device found on any serial port.");
                Console.Error.WriteLine("      Check that the device is powered, USB cable is data-capable, and drivers are installed.");
                return ExitCode.NoDevice;
            }

            if (discovered.Count > 1)
            {
                Console.WriteLine($"      Found {discovered.Count} devices; using the first. Pass --port=<name> to pick one.");
                foreach (var d in discovered)
                {
                    Console.WriteLine($"        - {d}");
                }
            }

            deviceInfo = discovered[0];
            Console.WriteLine($"      Discovered: {deviceInfo}");
        }
        else
        {
            Console.WriteLine($"Step 1/6  Using explicit port {options.PortName} (skipping discovery).");
            deviceInfo = null;
        }

        // ─── Step 2: connect + initialize ───────────────────────────────────
        Console.WriteLine("Step 2/6  Connecting and initializing device…");
        DaqifiDevice device;
        try
        {
            device = deviceInfo != null
                ? await DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(deviceInfo).ConfigureAwait(false)
                : await DaqifiDeviceFactory.ConnectSerialAsync(options.PortName, options.BaudRate).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL  connect/init failed: {ex.GetType().Name}: {ex.Message}");
            return ExitCode.ConnectFailed;
        }

        // From here on, ensure we always tear down the device and (best-effort)
        // stop streaming, even on assertion failure or thrown exceptions —
        // otherwise the device is left streaming and the next run starts with
        // a torrent of stale samples that masks real regressions.
        try
        {
            // ─── Step 3: read metadata ──────────────────────────────────────
            Console.WriteLine("Step 3/6  Reading device metadata…");
            var meta = device.Metadata;
            Console.WriteLine($"      Part number:      {Display(meta.PartNumber)}");
            Console.WriteLine($"      Serial number:    {Display(meta.SerialNumber)}");
            Console.WriteLine($"      Firmware version: {Display(meta.FirmwareVersion)}");
            Console.WriteLine($"      Hardware rev:     {Display(meta.HardwareRevision)}");
            Console.WriteLine($"      Device type:      {meta.DeviceType}");
            Console.WriteLine($"      Analog inputs:    {meta.Capabilities.AnalogInputChannels}");

            if (string.IsNullOrWhiteSpace(meta.PartNumber) || meta.DeviceType == DeviceType.Unknown)
            {
                Console.Error.WriteLine("FAIL  device initialized but reported empty/unknown part number — initialization did not populate metadata.");
                return ExitCode.MetadataMissing;
            }

            // ─── Step 4: subscribe + start streaming ────────────────────────
            // Count both protobuf "stream" messages and the analog-in samples
            // they carry. A device can emit messages without any analog data
            // (e.g. status frames), and a healthy stream produces both — we
            // want to be specific that we saw actual ADC data.
            var streamMessageCount = 0;
            var analogSampleCount = 0;
            void OnMessage(object? sender, MessageReceivedEventArgs e)
            {
                if (e.Message.Data is not DaqifiOutMessage msg) return;
                Interlocked.Increment(ref streamMessageCount);
                if (msg.AnalogInData.Count > 0)
                {
                    Interlocked.Add(ref analogSampleCount, msg.AnalogInData.Count);
                }
            }

            device.MessageReceived += OnMessage;
            try
            {
                Console.WriteLine($"Step 4/6  Enabling ADC channels (bitmask={options.ChannelBitmask}) and starting stream at {options.StreamRateHz} Hz…");
                device.Send(ScpiMessageProducer.EnableAdcChannels(options.ChannelBitmask));
                // Small gap so the channel-enable lands before the start-stream command
                // — some firmware revisions return -200 if these arrive in the same frame.
                await Task.Delay(100).ConfigureAwait(false);
                device.Send(ScpiMessageProducer.StartStreaming(options.StreamRateHz));

                // ─── Step 5: stream for N seconds ───────────────────────────
                Console.WriteLine($"Step 5/6  Streaming for {options.StreamDurationSeconds}s…");
                await Task.Delay(TimeSpan.FromSeconds(options.StreamDurationSeconds)).ConfigureAwait(false);

                device.Send(ScpiMessageProducer.StopStreaming);
                // Let the producer drain any in-flight bytes before we tear down.
                await Task.Delay(200).ConfigureAwait(false);
            }
            finally
            {
                device.MessageReceived -= OnMessage;
            }

            // ─── Step 6: verify ─────────────────────────────────────────────
            Console.WriteLine("Step 6/6  Verifying sample throughput…");
            Console.WriteLine($"      Protobuf messages received: {streamMessageCount}");
            Console.WriteLine($"      Analog samples received:    {analogSampleCount}");

            // Tolerate ~50% of the theoretical max — USB CDC framing,
            // initial channel-enable latency, and the stop window all eat
            // into the count. A real broken stream produces zero, not half.
            var expected = options.StreamRateHz * options.StreamDurationSeconds;
            var minAcceptable = Math.Max(1, expected / 2);

            if (analogSampleCount == 0)
            {
                Console.Error.WriteLine("FAIL  no analog samples received — streaming pipeline is broken.");
                return ExitCode.NoSamples;
            }
            if (analogSampleCount < minAcceptable)
            {
                Console.Error.WriteLine($"FAIL  sample count {analogSampleCount} below threshold {minAcceptable} (expected ~{expected}). Stream is degraded.");
                return ExitCode.DegradedSamples;
            }

            Console.WriteLine();
            Console.WriteLine("PASS  device discovered, connected, initialized, streamed, and stopped cleanly.");
            return ExitCode.Success;
        }
        finally
        {
            // Best-effort cleanup: stop streaming and dispose. We swallow
            // exceptions here because the primary failure mode is already
            // reflected in the exit code — we don't want cleanup noise to
            // mask the real cause.
            try { device.Send(ScpiMessageProducer.StopStreaming); } catch { }
            try { device.Disconnect(); } catch { }
            try { device.Dispose(); } catch { }
        }
    }

    private static string Display(string value) =>
        string.IsNullOrWhiteSpace(value) ? "<empty>" : value;

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Daqifi.Core.SmokeTest — hardware-in-the-loop smoke test (USB/serial)

            Usage:
              dotnet run --project src/Daqifi.Core.SmokeTest -- [options]

            Options:
              --port=<name|auto>    Serial port. 'auto' (default) runs discovery.
              --baud=<int>          Baud rate. Default: 9600.
              --rate=<hz>           Stream sample rate in Hz. Default: 100.
              --duration=<seconds>  Stream duration in seconds. Default: 2.
              --channels=<bitmask>  Decimal ADC channel bitmask. Default: 3 (channels 0,1).
              -h, --help            Show this help.

            Exit codes:
              0   success                 13  degraded samples
              2   bad arguments           99  unexpected error
              10  no device found
              11  connect or init failed
              12  metadata missing or unknown device type
              20  no samples received
            """);
    }

    private enum ExitCode
    {
        Success = 0,
        BadArgs = 2,
        NoDevice = 10,
        ConnectFailed = 11,
        MetadataMissing = 12,
        NoSamples = 20,
        DegradedSamples = 13,
        Unexpected = 99,
    }

    private sealed class Options
    {
        public string PortName { get; init; } = "auto";
        public int BaudRate { get; init; } = DefaultBaudRate;
        public int StreamRateHz { get; init; } = DefaultStreamRateHz;
        public int StreamDurationSeconds { get; init; } = DefaultStreamDurationSeconds;
        public string ChannelBitmask { get; init; } = DefaultChannelBitmask;
        public bool ShowHelp { get; init; }

        public static Options Parse(string[] args)
        {
            var port = "auto";
            var baud = DefaultBaudRate;
            var rate = DefaultStreamRateHz;
            var duration = DefaultStreamDurationSeconds;
            var channels = DefaultChannelBitmask;
            var help = false;

            foreach (var raw in args)
            {
                var arg = raw.Trim();
                if (arg is "-h" or "--help")
                {
                    help = true;
                    continue;
                }

                var (key, value) = SplitKeyValue(arg);
                switch (key)
                {
                    case "--port":
                        port = RequireValue(key, value);
                        break;
                    case "--baud":
                        baud = ParseInt(key, value, min: 1);
                        break;
                    case "--rate":
                        rate = ParseInt(key, value, min: 1);
                        break;
                    case "--duration":
                        duration = ParseInt(key, value, min: 1);
                        break;
                    case "--channels":
                        channels = RequireValue(key, value);
                        break;
                    default:
                        throw new ArgumentException($"unknown argument '{arg}'");
                }
            }

            return new Options
            {
                PortName = port,
                BaudRate = baud,
                StreamRateHz = rate,
                StreamDurationSeconds = duration,
                ChannelBitmask = channels,
                ShowHelp = help,
            };
        }

        private static (string Key, string? Value) SplitKeyValue(string arg)
        {
            var eq = arg.IndexOf('=');
            return eq < 0 ? (arg, null) : (arg[..eq], arg[(eq + 1)..]);
        }

        private static string RequireValue(string key, string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException($"{key} requires a value (e.g. {key}=value)")
                : value!;

        private static int ParseInt(string key, string? value, int min)
        {
            var raw = RequireValue(key, value);
            if (!int.TryParse(raw, out var n) || n < min)
            {
                throw new ArgumentException($"{key} must be an integer >= {min} (got '{raw}')");
            }
            return n;
        }
    }
}
