using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Resources;

/// <summary>
/// The board specifications in <see cref="HardwareCatalog"/>, served as MCP resources.
/// </summary>
/// <remarks>
/// <para>
/// One resource per board rather than one document listing all of them: a resource is the unit a
/// client lists and a user attaches, so three entries let someone hand their agent the board they
/// own instead of the whole product line.
/// </para>
/// <para>
/// Resources are how a <em>person</em> reaches this data — they pick it in the client and it
/// enters the conversation. A model cannot fetch one on its own in most clients, which is why the
/// same catalog is also a tool (<c>get_hardware_info</c>). Both read
/// <see cref="HardwareCatalog"/>, so neither can drift from the other. Neither is the authority
/// on a connected device's resolution or range: the device declares those and
/// <c>list_channels</c> reports them.
/// </para>
/// </remarks>
[McpServerResourceType]
public static class HardwareResources
{
    /// <summary>The published specifications of the Nyquist 1.</summary>
    [McpServerResource(
        UriTemplate = "daqifi://hardware/nyquist1",
        Name = "nyquist1_hardware",
        Title = "Nyquist 1 hardware",
        MimeType = "application/json")]
    [Description("DAQiFi Nyquist 1 specifications: 16 analog inputs, nominally 12-bit over 0 to 5 V, up to 19,250 Hz on one channel, 16 digital I/O, no analog outputs. " + HardwareCatalog.Provenance)]
    public static string Nyquist1() => Json("Nyquist1");

    /// <summary>The published specifications of the Nyquist 2.</summary>
    [McpServerResource(
        UriTemplate = "daqifi://hardware/nyquist2",
        Name = "nyquist2_hardware",
        Title = "Nyquist 2 hardware",
        MimeType = "application/json")]
    [Description("DAQiFi Nyquist 2 specifications: 16 analog inputs, nominally 24-bit (AD7173) over 0 to 5 V, and 8 DAC outputs this server does not drive. " + HardwareCatalog.Provenance)]
    public static string Nyquist2() => Json("Nyquist2");

    /// <summary>The published specifications of the Nyquist 3.</summary>
    [McpServerResource(
        UriTemplate = "daqifi://hardware/nyquist3",
        Name = "nyquist3_hardware",
        Title = "Nyquist 3 hardware",
        MimeType = "application/json")]
    [Description("DAQiFi Nyquist 3 specifications: 8 simultaneously-sampled analog inputs, nominally 18-bit (AD7609) over -10 to +10 V, and 8 DAC outputs this server can drive. " + HardwareCatalog.Provenance)]
    public static string Nyquist3() => Json("Nyquist3");

    /// <summary>
    /// The catalog entry for <paramref name="deviceType"/> as JSON.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The catalog has no entry for a board that has a resource here. Unreachable with the
    /// catalog and these three methods in agreement, and a loud failure is the right answer if
    /// one is ever renamed without the other: the alternative is a resource that quietly serves
    /// nothing.
    /// </exception>
    private static string Json(string deviceType)
    {
        var profile = HardwareCatalog.Find(deviceType)
            ?? throw new InvalidOperationException(
                $"No hardware profile for '{deviceType}'. A resource and HardwareCatalog.All have gone out of step.");

        return HardwareCatalog.ToJson(profile);
    }
}
