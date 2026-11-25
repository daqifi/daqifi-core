using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #931: every lifecycle change a device may make is in one table. These walk a real device
/// across each row, so a value added to the enum or an edge added to the table is covered without
/// anyone having to remember to write a test for it.
/// </summary>
public class DaqifiDeviceLifecycleTableTests
{
    /// <summary>
    /// The states a session is up in. Written out rather than taken from
    /// <see cref="DeviceStateExtensions.IsConnected"/>, because that is what is being checked.
    /// </summary>
    private static readonly HashSet<DeviceState> ConnectedStates = new()
    {
        DeviceState.Connected,
        DeviceState.Initializing,
        DeviceState.Ready,
        DeviceState.InitializationFailed,
        DeviceState.Disconnecting
    };

    private static IEnumerable<DeviceState> AllValues => Enum.GetValues<DeviceState>();

    public static TheoryData<string, string> PermittedChanges()
    {
        var data = new TheoryData<string, string>();
        foreach (var (from, targets) in DeviceLifecycleTable.AllowedTransitions)
        {
            foreach (var to in targets)
            {
                data.Add(from.ToString(), to.ToString());
            }
        }

        return data;
    }

    public static TheoryData<string, string> RefusedChanges()
    {
        var data = new TheoryData<string, string>();
        foreach (var from in AllValues)
        {
            foreach (var to in AllValues)
            {
                if (from != to && !DeviceLifecycleTable.IsAllowed(from, to))
                {
                    data.Add(from.ToString(), to.ToString());
                }
            }
        }

        return data;
    }

    [Fact]
    public void EveryLifecycleValueHasARowInTheTable()
    {
        Assert.Equal(
            AllValues.OrderBy(value => value),
            DeviceLifecycleTable.AllowedTransitions.Keys.OrderBy(value => value));
    }

    [Fact]
    public void EveryLifecycleValueCanBeReachedFromAFreshDevice()
    {
        foreach (var value in AllValues)
        {
            Assert.NotNull(PathTo(value));
        }
    }

    [Fact]
    public void OnlyDisposedIsTerminal()
    {
        foreach (var value in AllValues)
        {
            Assert.Equal(
                value == DeviceState.Disposed,
                DeviceLifecycleTable.AllowedTransitions[value].Count == 0);
        }
    }

    [Fact]
    public void IsConnected_IsTrueInExactlyTheStatesWithASession()
    {
        foreach (var value in AllValues)
        {
            Assert.Equal(ConnectedStates.Contains(value), value.IsConnected());
        }
    }

    [Theory]
    [MemberData(nameof(PermittedChanges))]
    public void APermittedChange_RaisesOneEventWithBothValues(string fromName, string toName)
    {
        var from = Enum.Parse<DeviceState>(fromName);
        var to = Enum.Parse<DeviceState>(toName);
        using var device = DeviceOn(from);

        var raised = new List<(DeviceState Previous, DeviceState Current, DeviceState State, bool IsConnected)>();
        device.StateChanged += (_, e) =>
            raised.Add((e.PreviousState, e.CurrentState, device.State, device.IsConnected));

        Assert.True(device.TryTransition(to));

        // A handler reads the state the event reports, with everything already settled.
        Assert.Equal([(from, to, to, ConnectedStates.Contains(to))], raised);
        AssertAgreement(device, to);
    }

    [Theory]
    [MemberData(nameof(RefusedChanges))]
    public void AChangeThatIsNotInTheTable_IsRefusedAndChangesNothing(string fromName, string toName)
    {
        var from = Enum.Parse<DeviceState>(fromName);
        var to = Enum.Parse<DeviceState>(toName);
        using var device = DeviceOn(from);

        var raised = 0;
        device.StateChanged += (_, _) => raised++;

        Assert.False(device.TryTransition(to));

        Assert.Equal(from, device.State);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void AChangeToTheCurrentValue_SucceedsAndRaisesNothing()
    {
        foreach (var value in AllValues)
        {
            using var device = DeviceOn(value);
            var raised = 0;
            device.StateChanged += (_, _) => raised++;

            Assert.True(device.TryTransition(value));

            Assert.Equal(value, device.State);
            Assert.Equal(0, raised);
        }
    }

    [Fact]
    public void ADisposedDevice_RefusesToConnect()
    {
        var device = new DaqifiDevice("Device");
        device.Dispose();
        Assert.Equal(DeviceState.Disposed, device.State);

        Assert.Throws<ObjectDisposedException>(() => device.Connect());

        AssertAgreement(device, DeviceState.Disposed);
    }

    private static void AssertAgreement(DaqifiDevice device, DeviceState expected)
    {
        Assert.Equal(expected, device.State);
        Assert.Equal(ConnectedStates.Contains(expected), device.IsConnected);
    }

    /// <summary>A real device, walked to <paramref name="target"/> over the table's own edges.</summary>
    private static DaqifiDevice DeviceOn(DeviceState target)
    {
        var device = new DaqifiDevice("Device");
        foreach (var step in PathTo(target)!)
        {
            Assert.True(device.TryTransition(step));
            AssertAgreement(device, step);
        }

        return device;
    }

    /// <summary>The shortest walk over the table from a fresh device, or null if there is none.</summary>
    private static IReadOnlyList<DeviceState>? PathTo(DeviceState target)
    {
        var cameFrom = new Dictionary<DeviceState, DeviceState>();
        var pending = new Queue<DeviceState>();
        pending.Enqueue(DeviceState.Disconnected);
        var seen = new HashSet<DeviceState> { DeviceState.Disconnected };

        while (pending.TryDequeue(out var current))
        {
            if (current == target)
            {
                var path = new List<DeviceState>();
                for (var step = current; step != DeviceState.Disconnected; step = cameFrom[step])
                {
                    path.Add(step);
                }

                path.Reverse();
                return path;
            }

            foreach (var next in DeviceLifecycleTable.AllowedTransitions[current])
            {
                if (seen.Add(next))
                {
                    cameFrom[next] = current;
                    pending.Enqueue(next);
                }
            }
        }

        return null;
    }
}
