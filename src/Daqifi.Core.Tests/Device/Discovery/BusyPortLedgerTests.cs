using Daqifi.Core.Device.Discovery;
using System.Linq;
using Xunit;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// The ledger's whole job is to stay correct when probes finish out of order, so these drive the
/// out-of-order cases directly rather than through a real discovery pass.
/// </summary>
public class BusyPortLedgerTests
{
    private static BusyPort Port(string name, string? location = null) => new(name, location);

    [Fact]
    public void Record_WhenALateProbeAlreadyRepopulatedThePort_TheCurrentPassStillWins()
    {
        // THE regression. A probe from pass N-1 abandoned on the timeout keeps running and reaches
        // its catch AFTER BeginPass cleared the set, putting a stale entry back under the same
        // port name. If the current pass's write is then dropped -- which is exactly what TryAdd
        // does when the key is present -- the port is missing from the current pass's set, the
        // device holding it is not rescued, and it is reported lost. That is the bug this whole
        // mechanism exists to prevent, arriving through the back door.
        var ledger = new BusyPortLedger();
        var stalePass = ledger.BeginPass();
        var currentPass = ledger.BeginPass();

        ledger.Record(stalePass, Port("COM3"));      // late probe, lands after the clear
        ledger.Record(currentPass, Port("COM3"));    // this pass's own observation

        Assert.Equal(new[] { "COM3" }, ledger.TakePortsFromLastPass().Select(p => p.PortName));
    }

    [Fact]
    public void Record_WhenALateProbeArrivesAfterTheCurrentPass_ItDoesNotClobberIt()
    {
        // The same race in the other order, which is why "last writer wins" would also be wrong.
        // A stale write landing second must not displace the current pass's entry.
        var ledger = new BusyPortLedger();
        var stalePass = ledger.BeginPass();
        var currentPass = ledger.BeginPass();

        ledger.Record(currentPass, Port("COM3", "usb:1-1.2"));
        ledger.Record(stalePass, Port("COM3", "usb:9-9.9"));

        var ports = ledger.TakePortsFromLastPass();
        Assert.Equal("usb:1-1.2", Assert.Single(ports).LocationKey);
    }

    [Fact]
    public void PortsFromLastPass_IgnoresEntriesFromAnEarlierPass()
    {
        var ledger = new BusyPortLedger();
        var first = ledger.BeginPass();
        ledger.Record(first, Port("COM3"));

        ledger.BeginPass();

        // A port freed since the last pass must not stay "busy" -- otherwise a device really
        // unplugged while its name was occupied would be rescued forever.
        Assert.Empty(ledger.TakePortsFromLastPass());
    }

    [Fact]
    public void Record_TreatsPortNamesCaseInsensitively()
    {
        // An OS port name is not case-significant. Two spellings becoming two entries would show
        // up as a phantom second busy port.
        var ledger = new BusyPortLedger();
        var pass = ledger.BeginPass();

        ledger.Record(pass, Port("COM3"));
        ledger.Record(pass, Port("com3"));

        Assert.Single(ledger.TakePortsFromLastPass());
    }

    [Fact]
    public void TakePortsFromLastPass_IsSingleUse()
    {
        // A busy-port set describes ONE pass. A discovery run across several transports can return
        // before this finder ever began a pass, and the newest data here would then be from an
        // older moment -- so a second take without an intervening pass must report nothing rather
        // than hand out the same observation again. Repeated contention would otherwise reuse one
        // stale location-confirmed entry forever, and that path is deliberately unbounded, so a
        // device that really was unplugged would never be reported lost.
        var ledger = new BusyPortLedger();
        var pass = ledger.BeginPass();
        ledger.Record(pass, Port("COM3"));

        Assert.Single(ledger.TakePortsFromLastPass());
        Assert.Empty(ledger.TakePortsFromLastPass());
    }

    [Fact]
    public void TakePortsFromLastPass_IsRearmedByANewPass()
    {
        // The complement: taking must not disable the ledger, or the feature stops working after
        // the first reconcile.
        var ledger = new BusyPortLedger();
        var first = ledger.BeginPass();
        ledger.Record(first, Port("COM3"));
        ledger.TakePortsFromLastPass();

        var second = ledger.BeginPass();
        ledger.Record(second, Port("COM3"));

        Assert.Single(ledger.TakePortsFromLastPass());
    }

    [Fact]
    public void TakePortsFromLastPass_BeforeAnyPass_IsEmpty()
    {
        Assert.Empty(new BusyPortLedger().TakePortsFromLastPass());
    }

    [Fact]
    public void CurrentPass_AdvancesWithEachPass()
    {
        var ledger = new BusyPortLedger();
        var first = ledger.BeginPass();
        var second = ledger.BeginPass();

        Assert.NotEqual(first, second);
        Assert.Equal(second, ledger.CurrentPass);
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(1, 2, false)]
    [InlineData(1, 1, false)]
    // The wrap. A plain `a > b` inverts here and would drop a legitimate rescue for one pass;
    // int.MinValue is the pass that immediately FOLLOWS int.MaxValue.
    [InlineData(int.MinValue, int.MaxValue, true)]
    [InlineData(int.MaxValue, int.MinValue, false)]
    public void IsNewerPass_IsWrapSafe(int candidate, int existing, bool expected)
    {
        Assert.Equal(expected, BusyPortLedger.IsNewerPass(candidate, existing));
    }
}
