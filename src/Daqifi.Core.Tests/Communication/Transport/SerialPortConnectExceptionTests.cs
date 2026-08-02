using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Pins the connect-failure translation added for #424: <see cref="SerialStreamTransport"/> no
/// longer forwards the platform's exception for a failed open, because that exception cannot be
/// classified. Measured on macOS with System.IO.Ports 10.0.10, a missing port and a port held by
/// another process both produce
/// <c>UnauthorizedAccessException("Access to the port 'X' is denied.")</c> wrapping an
/// <c>IOException("Unknown error: 203")</c> — identical type, text, and HResult — so the reason has
/// to come from evidence gathered around the failure instead.
/// </summary>
public class SerialPortConnectExceptionTests
{
    /// <summary>
    /// The exact exception shape a failed open produces on macOS/Linux, for every cause.
    /// </summary>
    private static UnauthorizedAccessException PlatformDenied(string portName = "/dev/cu.fake") =>
        new($"Access to the port '{portName}' is denied.", new IOException("Unknown error: 203"));

    [Fact]
    public void Classify_WhenPortIsAbsent_ReportsNotFound()
    {
        // The reported bug: a port that does not exist was called an access denial.
        var reason = SerialPortConnectException.Classify(PlatformDenied(), portPresent: false,
            permissionGateRuledOut: null);

        Assert.Equal(SerialPortConnectFailure.NotFound, reason);
    }

    [Fact]
    public void Classify_WhenPortIsAbsent_IgnoresThePermissionGate()
    {
        // Absence is conclusive; a device node that has already vanished cannot be a permission
        // or an exclusivity problem, whatever the gate probe managed to say.
        Assert.Equal(SerialPortConnectFailure.NotFound,
            SerialPortConnectException.Classify(PlatformDenied(), false, permissionGateRuledOut: true));
        Assert.Equal(SerialPortConnectFailure.NotFound,
            SerialPortConnectException.Classify(PlatformDenied(), false, permissionGateRuledOut: false));
    }

    [Fact]
    public void Classify_FileNotFoundException_ReportsNotFoundWithoutCorroboration()
    {
        // Windows names this case outright, so it needs no presence probe to agree with it.
        var reason = SerialPortConnectException.Classify(
            new FileNotFoundException("The port 'COM254' does not exist."),
            portPresent: true,
            permissionGateRuledOut: true);

        Assert.Equal(SerialPortConnectFailure.NotFound, reason);
    }

    [Fact]
    public void Classify_FileNotFoundExceptionNestedInside_ReportsNotFound()
    {
        var reason = SerialPortConnectException.Classify(
            new UnauthorizedAccessException("wrapped", new FileNotFoundException("no such port")),
            portPresent: true,
            permissionGateRuledOut: true);

        Assert.Equal(SerialPortConnectFailure.NotFound, reason);
    }

    [Fact]
    public void Classify_PresentAndDeniedWhereNoPermissionGateCanApply_ReportsInUse()
    {
        // A macOS /dev/cu.* node is crw-rw-rw-, so nobody can be denied on permission grounds:
        // the port exists and refuses to open because another process holds it.
        var reason = SerialPortConnectException.Classify(PlatformDenied(), portPresent: true,
            permissionGateRuledOut: true);

        Assert.Equal(SerialPortConnectFailure.InUse, reason);
    }

    [Fact]
    public void Classify_PresentAndDeniedWhereAPermissionGateCouldApply_ReportsAccessDenied()
    {
        // A Linux dialout-owned node at crw-rw---- is the genuine permission case.
        var reason = SerialPortConnectException.Classify(PlatformDenied("/dev/ttyUSB0"),
            portPresent: true, permissionGateRuledOut: false);

        Assert.Equal(SerialPortConnectFailure.AccessDenied, reason);
    }

    [Fact]
    public void Classify_WhenTheGateCannotBeDetermined_DegradesToAccessDenied()
    {
        // An unfamiliar platform keeps today's wording rather than asserting something false.
        var reason = SerialPortConnectException.Classify(PlatformDenied(), portPresent: true,
            permissionGateRuledOut: null);

        Assert.Equal(SerialPortConnectFailure.AccessDenied, reason);
    }

    [Fact]
    public void Classify_WhenPresenceCannotBeObserved_DoesNotInventAbsence()
    {
        // A probe that could not answer is not evidence the port is gone. Reporting NotFound here
        // would turn any unrelated connect failure into a bogus "port was not found".
        var reason = SerialPortConnectException.Classify(PlatformDenied(), portPresent: null,
            permissionGateRuledOut: false);

        Assert.Equal(SerialPortConnectFailure.AccessDenied, reason);
    }

    [Fact]
    public void Classify_AnUnrecognizedIoFailure_StaysUnknown()
    {
        var reason = SerialPortConnectException.Classify(new IOException("the port hardware failed"),
            portPresent: true, permissionGateRuledOut: true);

        Assert.Equal(SerialPortConnectFailure.Unknown, reason);
    }

    [Fact]
    public void Classify_NullError_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SerialPortConnectException.Classify(null!, true, true));
    }

    [Theory]
    [InlineData(SerialPortConnectFailure.NotFound, "was not found")]
    [InlineData(SerialPortConnectFailure.InUse, "is in use")]
    [InlineData(SerialPortConnectFailure.AccessDenied, "is denied")]
    [InlineData(SerialPortConnectFailure.Unknown, "could not be opened")]
    public void DescribeFailure_NamesThePortAndTheCause(SerialPortConnectFailure reason, string expected)
    {
        var message = SerialPortConnectException.DescribeFailure("/dev/cu.usbmodem1101", reason);

        Assert.Contains("/dev/cu.usbmodem1101", message);
        Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFailure_AccessDenied_KeepsTheFrameworkWording()
    {
        // The one case the platform always got right; anything already keying on that phrasing for
        // a real permission problem keeps matching.
        Assert.Equal("Access to the port '/dev/ttyUSB0' is denied.",
            SerialPortConnectException.DescribeFailure("/dev/ttyUSB0", SerialPortConnectFailure.AccessDenied));
    }

    [Fact]
    public void FromOpenFailure_PreservesTheOriginalAsInnerException()
    {
        var original = PlatformDenied();

        var ex = SerialPortConnectException.FromOpenFailure("/dev/cu.fake", original,
            portPresent: false, permissionGateRuledOut: null);

        Assert.Same(original, ex.InnerException);
        Assert.Equal("/dev/cu.fake", ex.PortName);
        Assert.Equal(SerialPortConnectFailure.NotFound, ex.Reason);
        Assert.Contains("was not found", ex.Message);
    }

    [Theory]
    [InlineData(false, null, SerialPortConnectFailure.NotFound)]
    [InlineData(true, true, SerialPortConnectFailure.InUse)]
    [InlineData(true, false, SerialPortConnectFailure.AccessDenied)]
    public void FromOpenFailure_KeepsTheInnerExceptionForEveryReason(
        bool present, bool? gateRuledOut, SerialPortConnectFailure expected)
    {
        var original = PlatformDenied();

        var ex = SerialPortConnectException.FromOpenFailure("/dev/cu.fake", original, present, gateRuledOut);

        Assert.Equal(expected, ex.Reason);
        Assert.Same(original, ex.InnerException);
    }

    [Fact]
    public void SerialPortConnectException_IsAnIoException()
    {
        // Failing to open an I/O device is an I/O error, and Windows already reports a missing port
        // as an IOException-derived type, so catch (IOException) around a connect keeps working.
        var ex = new SerialPortConnectException("COM3", SerialPortConnectFailure.NotFound, "nope");

        Assert.IsAssignableFrom<IOException>(ex);
    }

    [Fact]
    public void SerialPortConnectException_RequiresAPortName()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SerialPortConnectException(null!, SerialPortConnectFailure.NotFound, "nope"));
    }
}
