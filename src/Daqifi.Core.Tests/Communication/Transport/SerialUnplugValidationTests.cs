using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Hardware-in-the-loop validation for issue #382: a human pulls a real USB cable and this
/// measures how long Core takes to report <see cref="ConnectionStatus.Lost"/>.
/// </summary>
/// <remarks>
/// <para>
/// Does nothing unless <c>DAQIFI_UNPLUG_PORT</c> names a serial port, so CI and ordinary local
/// runs are unaffected. Each test needs its own unplug, so run them one at a time, plugging the
/// device back in between:
/// </para>
/// <code>
/// DAQIFI_UNPLUG_PORT=/dev/cu.usbmodem1101 \
///   dotnet test src/Daqifi.Core.Tests/Daqifi.Core.Tests.csproj -f net9.0 \
///   --filter "FullyQualifiedName~UnpluggedSerialDevice_ReportsConnectionLost" \
///   --logger "console;verbosity=detailed"
///
/// DAQIFI_UNPLUG_PORT=/dev/cu.usbmodem1101 \
///   dotnet test src/Daqifi.Core.Tests/Daqifi.Core.Tests.csproj -f net9.0 \
///   --filter "FullyQualifiedName~UnpluggedSerialDevice_IsPrunedFromTheRegistry" \
///   --logger "console;verbosity=detailed"
/// </code>
/// <para>
/// The first connects, holds the connection open for a warm-up window that must produce no status
/// change at all (no false positives), then prints <c>&gt;&gt;&gt; UNPLUG THE CABLE NOW</c> and
/// waits. It watches the device node itself, so the reported detection latency is measured from
/// the moment the port actually vanished — not from the prompt. The second re-verifies PR #381's
/// registry pruning against the same event. Environment overrides:
/// <c>DAQIFI_UNPLUG_BAUD</c> (default 115200), <c>DAQIFI_UNPLUG_WARMUP_SECONDS</c> (default 20),
/// <c>DAQIFI_UNPLUG_TIMEOUT_SECONDS</c> (default 180).
/// </para>
/// </remarks>
public class SerialUnplugValidationTests
{
    private readonly ITestOutputHelper _output;

    public SerialUnplugValidationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void UnpluggedSerialDevice_ReportsConnectionLost_WithinTheDocumentedBound()
    {
        if (!TryGetHardwarePort(out var portName))
        {
            return;
        }

        var baudRate = ReadInt("DAQIFI_UNPLUG_BAUD", 115200);
        var warmup = TimeSpan.FromSeconds(ReadInt("DAQIFI_UNPLUG_WARMUP_SECONDS", 20));
        var timeout = TimeSpan.FromSeconds(ReadInt("DAQIFI_UNPLUG_TIMEOUT_SECONDS", 180));

        var transport = new SerialStreamTransport(portName, baudRate);
        using var device = new DaqifiDevice("Unplug Validation", transport);

        var statuses = new List<(DateTime At, ConnectionStatus Status)>();
        var lost = new ManualResetEventSlim(false);
        var lostAt = DateTime.MinValue;

        device.StatusChanged += (_, e) =>
        {
            var now = DateTime.UtcNow;
            lock (statuses)
            {
                statuses.Add((now, e.Status));
            }

            Log($"StatusChanged: {e.Status}");

            if (e.Status == ConnectionStatus.Lost)
            {
                lostAt = now;
                lost.Set();
            }
        };

        Log($"Connecting to {portName} @ {baudRate} baud...");
        device.Connect();
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Log("Connected.");

        // Phase 1 — a healthy connection must produce no status change at all. This is where a
        // liveness check that is too eager, or fault escalation that is too twitchy, shows up.
        Log($"Holding the connection open for {warmup.TotalSeconds:0}s; no status change is expected.");
        var beforeWarmup = SnapshotCount(statuses);
        Thread.Sleep(warmup);
        Assert.Equal(beforeWarmup, SnapshotCount(statuses));
        Assert.Equal(ConnectionStatus.Connected, device.Status);
        Assert.True(device.IsConnected);
        Log("No false positives during the healthy window.");

        // Phase 2 — the human pulls the cable. Watch the device node so detection latency is
        // measured from when the port really went away.
        using var watcher = new PortDisappearanceWatcher(portName);

        Log(string.Empty);
        Log(">>> UNPLUG THE CABLE NOW <<<");
        Log($"    (waiting up to {timeout.TotalSeconds:0}s for ConnectionStatus.Lost)");
        Log(string.Empty);

        var sawLost = lost.Wait(timeout);

        var portGoneAt = watcher.PortGoneAtUtc;
        if (portGoneAt.HasValue)
        {
            Log($"Port vanished from the system at {portGoneAt:HH:mm:ss.fff}Z");
        }

        Assert.True(sawLost,
            $"No ConnectionStatus.Lost within {timeout.TotalSeconds:0}s. " +
            (portGoneAt.HasValue
                ? "The port did disappear, so detection failed."
                : "The port never disappeared — was the cable actually unplugged?"));

        Log($"ConnectionStatus.Lost observed at {lostAt:HH:mm:ss.fff}Z");

        if (portGoneAt.HasValue)
        {
            var detection = lostAt - portGoneAt.Value;
            Log($"DETECTION LATENCY: {detection.TotalMilliseconds:0} ms");

            // The documented bound is ~3s (1s poll cadence x 2 consecutive misses, plus up to one
            // interval of phase). Assert with slack so a loaded machine does not produce a false
            // failure; the printed number above is the real result.
            Assert.True(detection < TimeSpan.FromSeconds(10),
                $"Detection took {detection.TotalSeconds:0.00}s, well past the documented ~3s bound.");
        }

        Assert.Equal(ConnectionStatus.Lost, device.Status);
        Assert.False(device.IsConnected);
        Assert.False(transport.IsConnected);

        lock (statuses)
        {
            Assert.DoesNotContain(ConnectionStatus.Disconnected, statuses.Select(s => s.Status));
        }

        Log("PASS — unplug reported as Lost, not Disconnected.");
    }

    [Fact]
    public void UnpluggedSerialDevice_IsPrunedFromTheRegistry()
    {
        // Re-verifies PR #381's stale-registration pruning against a real unplug: the registry
        // prunes registrations whose device stops reporting IsConnected, which before #382 never
        // happened for a physically unplugged USB device.
        if (!TryGetHardwarePort(out var portName))
        {
            return;
        }

        var baudRate = ReadInt("DAQIFI_UNPLUG_BAUD", 115200);
        var timeout = TimeSpan.FromSeconds(ReadInt("DAQIFI_UNPLUG_TIMEOUT_SECONDS", 180));

        using var registry = new DaqifiDeviceRegistry();
        var transport = new SerialStreamTransport(portName, baudRate);
        var device = new DaqifiDevice("Unplug Validation", transport);

        var removals = new List<DeviceRemovalReason>();
        registry.DeviceRemoved += (_, e) =>
        {
            lock (removals)
            {
                removals.Add(e.Reason);
            }
        };

        device.Connect();
        var registration = registry.Register(device, key: "unplug-validation");
        Assert.Equal(DeviceRegistrationOutcome.Registered, registration.Outcome);
        Assert.Single(registry.Devices);

        Log(string.Empty);
        Log(">>> UNPLUG THE CABLE NOW <<<");
        Log(string.Empty);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && device.IsConnected)
        {
            Thread.Sleep(100);
        }

        Assert.False(device.IsConnected, $"Device still reported IsConnected after {timeout.TotalSeconds:0}s.");
        Log($"Device stopped reporting IsConnected after {sw.Elapsed.TotalSeconds:0.00}s.");

        Assert.Equal(1, registry.PruneDisconnected());
        Assert.Empty(registry.Devices);

        lock (removals)
        {
            Assert.Contains(DeviceRemovalReason.Disconnected, removals);
        }

        Log("PASS — the unplugged device was pruned from the registry.");
    }

    private static int SnapshotCount(List<(DateTime At, ConnectionStatus Status)> statuses)
    {
        lock (statuses)
        {
            return statuses.Count;
        }
    }

    /// <summary>
    /// Resolves the port to validate against, or reports that this run is not a hardware run.
    /// </summary>
    /// <remarks>
    /// xUnit 2.x has no dynamic skip (<c>Assert.Skip</c> arrived in v3), and a statically skipped
    /// <c>[Fact(Skip = ...)]</c> cannot be turned on by the operator at all — so an unconfigured
    /// run logs why it did nothing and returns. The hardware assertions below are the point of the
    /// test; they run whenever <c>DAQIFI_UNPLUG_PORT</c> is set.
    /// </remarks>
    private bool TryGetHardwarePort(out string portName)
    {
        portName = Environment.GetEnvironmentVariable("DAQIFI_UNPLUG_PORT") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(portName))
        {
            return true;
        }

        Log("SKIPPED — set DAQIFI_UNPLUG_PORT to a connected DAQiFi serial port to run this " +
            "hardware validation (see the class remarks for the full command line).");
        return false;
    }

    private static int ReadInt(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    private void Log(string message)
    {
        var line = message.Length == 0 ? string.Empty : $"[{DateTime.UtcNow:HH:mm:ss.fff}Z] {message}";
        _output.WriteLine(line);
        Console.WriteLine(line);
    }

    /// <summary>
    /// Records the moment the serial port stops being present, so detection latency is measured
    /// from the unplug itself rather than from the operator's prompt.
    /// </summary>
    private sealed class PortDisappearanceWatcher : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        private DateTime? _portGoneAtUtc;

        public PortDisappearanceWatcher(string portName)
        {
            _thread = new Thread(() =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (!SerialStreamTransport.IsPortEnumerated(portName))
                    {
                        _portGoneAtUtc = DateTime.UtcNow;
                        return;
                    }

                    Thread.Sleep(20);
                }
            })
            {
                IsBackground = true,
                Name = "PortDisappearanceWatcher"
            };

            _thread.Start();
        }

        public DateTime? PortGoneAtUtc => _portGoneAtUtc;

        public void Dispose()
        {
            _cts.Cancel();
            _thread.Join(TimeSpan.FromSeconds(2));
            _cts.Dispose();
        }
    }
}
