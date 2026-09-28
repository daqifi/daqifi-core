using System.IO.Ports;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Port names checked to be absent on the running host, so missing-port tests assert the
/// <see cref="Daqifi.Core.Communication.Transport.SerialPortConnectFailure.NotFound"/> translation
/// and nothing else.
/// </summary>
public static class AbsentSerialPorts
{
    /// <summary>
    /// Produces a port name checked to be absent on the running host.
    /// </summary>
    /// <remarks>
    /// Verified at runtime rather than hard-coded: a fixed name is only absent by assumption, and a
    /// host that happens to have it — a virtual COM port, a leftover device node — would make these
    /// tests either open real hardware or fail with a different error shape.
    /// </remarks>
    public static string Create()
    {
        // Whether the enumeration *answered* is tracked separately from what it returned, because
        // the two failure modes are not equivalent and must not be collapsed. An empty result is a
        // real answer — the host has no serial ports. A throw is no answer at all.
        bool enumerationAnswered;
        HashSet<string> enumerated;
        try
        {
            enumerated = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
            enumerationAnswered = true;
        }
        catch (Exception)
        {
            // Enumeration can throw on some hosts (a container without /dev access, a locked-down
            // machine). It must not take the suite down from inside a test helper — that reads as
            // the feature under test breaking rather than the environment.
            enumerated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            enumerationAnswered = false;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows has no absence check independent of the enumeration: a COM name is not a
            // filesystem path, so there is no File.Exists equivalent, and the enumeration is itself
            // the authority (it reads the HARDWARE\DEVICEMAP\SERIALCOMM registry map). That makes
            // an answered enumeration sufficient — including an empty one, which positively means
            // no COM ports exist — but leaves a *failed* enumeration with no evidence whatsoever.
            // Choosing a name in that state would assert against an unverified port and could pass
            // or fail for reasons unrelated to the translation being tested, so refuse instead.
            if (!enumerationAnswered)
            {
                throw new InvalidOperationException(
                    "Cannot verify an absent COM name: SerialPort.GetPortNames() failed and Windows " +
                    "offers no independent check that a COM name is unused. Refusing to assert " +
                    "against an unverified port.");
            }

            // The Windows serial stack only accepts COM-prefixed names, so a random suffix is not
            // an option; take the highest COM number the enumeration does not claim.
            for (var number = 255; number >= 200; number--)
            {
                var candidate = $"COM{number}";
                if (!enumerated.Contains(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                "No unused COM name available to exercise the missing-port path.");
        }

        // Unix needs no such guard: the port name is a filesystem path, so File.Exists answers
        // independently of the enumeration and a failed enumeration costs the check nothing.

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = $"/dev/tty.daqifi-core-absent-424-{Guid.NewGuid():N}";
            if (!File.Exists(candidate) && !enumerated.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not generate an absent device node path to exercise the missing-port path.");
    }
}
