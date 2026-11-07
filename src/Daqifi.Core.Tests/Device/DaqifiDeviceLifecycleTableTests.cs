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
    /// Every pair of public values that tell one story. Written out rather than taken from
    /// <see cref="DeviceLifecycleTable"/>, because the mapping there is what is being checked.
    /// </summary>
    private static readonly HashSet<(ConnectionStatus, DeviceState)> AgreeingPairs = new()
    {
        (ConnectionStatus.Disconnected, DeviceState.Disconnected),
        (ConnectionStatus.Connecting, DeviceState.Connecting),
        (ConnectionStatus.Connected, DeviceState.Connected),
        (ConnectionStatus.Connected, DeviceState.Initializing),
        (ConnectionStatus.Connected, DeviceState.Ready),
        (ConnectionStatus.Connected, DeviceState.Error),
        (ConnectionStatus.Lost, DeviceState.Disconnected),
        (ConnectionStatus.Retrying, DeviceState.Disconnected),
        (ConnectionStatus.Failed, DeviceState.Disconnected)
    };

    private static IEnumerable<DeviceLifecycle> AllValues => Enum.GetValues<DeviceLifecycle>();

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
                value == DeviceLifecycle.Disposed,
                DeviceLifecycleTable.AllowedTransitions[value].Count == 0);
        }
    }

    [Fact]
    public void TheStatusAndStateOfAConnectionWithNoSession_ReadDisconnected()
    {
        foreach (var value in new[] { DeviceLifecycle.Lost, DeviceLifecycle.Retrying, DeviceLifecycle.Failed })
        {
            Assert.Equal(DeviceState.Disconnected, DeviceLifecycleTable.ToState(value));
        }
    }

    [Theory]
    [MemberData(nameof(PermittedChanges))]
    public void APermittedChange_LeavesStatusStateAndIsConnectedInAgreement(string fromName, string toName)
    {
        var from = Enum.Parse<DeviceLifecycle>(fromName);
        var to = Enum.Parse<DeviceLifecycle>(toName);
        using var device = DeviceOn(from);

        var statusBefore = device.Status;
        var raised = new List<(ConnectionStatus Reported, ConnectionStatus Status, DeviceState State)>();
        device.StatusChanged += (_, e) => raised.Add((e.Status, device.Status, device.State));

        Assert.True(device.TryTransition(to));

        Assert.Equal(to, device.Lifecycle);
        AssertAgreement(device);

        if (device.Status == statusBefore)
        {
            Assert.Empty(raised);
        }
        else
        {
            // A handler reads the state the event reports, with everything already settled.
            var single = Assert.Single(raised);
            Assert.Equal(device.Status, single.Reported);
            Assert.Equal(device.Status, single.Status);
            Assert.Equal(device.State, single.State);
        }
    }

    [Theory]
    [MemberData(nameof(RefusedChanges))]
    public void AChangeThatIsNotInTheTable_IsRefusedAndChangesNothing(string fromName, string toName)
    {
        var from = Enum.Parse<DeviceLifecycle>(fromName);
        var to = Enum.Parse<DeviceLifecycle>(toName);
        using var device = DeviceOn(from);

        var statusBefore = device.Status;
        var stateBefore = device.State;
        var raised = 0;
        device.StatusChanged += (_, _) => raised++;

        Assert.False(device.TryTransition(to));

        Assert.Equal(from, device.Lifecycle);
        Assert.Equal(statusBefore, device.Status);
        Assert.Equal(stateBefore, device.State);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void AChangeToTheCurrentValue_SucceedsAndRaisesNothing()
    {
        foreach (var value in AllValues)
        {
            using var device = DeviceOn(value);
            var raised = 0;
            device.StatusChanged += (_, _) => raised++;

            Assert.True(device.TryTransition(value));

            Assert.Equal(value, device.Lifecycle);
            Assert.Equal(0, raised);
        }
    }

    [Fact]
    public void ADisposedDevice_RefusesToConnect()
    {
        var device = new DaqifiDevice("Device");
        device.Dispose();
        Assert.Equal(DeviceLifecycle.Disposed, device.Lifecycle);

        Assert.Throws<ObjectDisposedException>(() => device.Connect());

        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal(DeviceState.Disconnected, device.State);
    }

    private static void AssertAgreement(DaqifiDevice device)
    {
        Assert.Contains((device.Status, device.State), AgreeingPairs);
        Assert.Equal(device.Status == ConnectionStatus.Connected, device.IsConnected);
    }

    /// <summary>A real device, walked to <paramref name="target"/> over the table's own edges.</summary>
    private static DaqifiDevice DeviceOn(DeviceLifecycle target)
    {
        var device = new DaqifiDevice("Device");
        foreach (var step in PathTo(target)!)
        {
            Assert.True(device.TryTransition(step));
            AssertAgreement(device);
        }

        return device;
    }

    /// <summary>The shortest walk over the table from a fresh device, or null if there is none.</summary>
    private static IReadOnlyList<DeviceLifecycle>? PathTo(DeviceLifecycle target)
    {
        var cameFrom = new Dictionary<DeviceLifecycle, DeviceLifecycle>();
        var pending = new Queue<DeviceLifecycle>();
        pending.Enqueue(DeviceLifecycle.Disconnected);
        var seen = new HashSet<DeviceLifecycle> { DeviceLifecycle.Disconnected };

        while (pending.TryDequeue(out var current))
        {
            if (current == target)
            {
                var path = new List<DeviceLifecycle>();
                for (var step = current; step != DeviceLifecycle.Disconnected; step = cameFrom[step])
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
