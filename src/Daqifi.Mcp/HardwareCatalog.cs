using System.Text.Json;
using Daqifi.Mcp.Tools;

namespace Daqifi.Mcp;

/// <summary>
/// The published specifications of each Nyquist board, as static reference data.
/// </summary>
/// <remarks>
/// <para>
/// Nothing a device reports over the wire says how many bits its ADC has, what input range it
/// accepts, or how fast it can be driven. An agent could therefore read a value and have no way
/// to tell a 12-bit measurement from a 24-bit one, and had to discover the sample-rate ceiling by
/// provoking a rejection from <c>set_sample_rate</c>. The product pages carry those facts; this
/// is the same data where the server can serve it, as MCP resources and
/// <c>get_hardware_info</c>.
/// </para>
/// <para>
/// This is a catalog, not a measurement: it describes the board model, not the particular unit on
/// the bench. Anything the connected device can actually answer for itself — its channel counts,
/// which digital channels are PWM-capable, which DAC channels its capability document described —
/// is deliberately left out and stays with <c>list_channels</c> and
/// <c>list_connected_devices</c>, so there is one source of truth per fact. A field this catalog
/// does not know is null rather than guessed.
/// </para>
/// </remarks>
public static class HardwareCatalog
{
    /// <summary>
    /// The sentence that every description of this data repeats: it is published specification,
    /// not something read off the device.
    /// </summary>
    public const string Provenance =
        "Static published specifications for the board model, not a reading from the connected unit; "
        + "call list_channels for the channels a connected device actually reports.";

    /// <summary>Every board this server has specifications for, in model order.</summary>
    public static IReadOnlyList<HardwareProfile> All { get; } =
    [
        new HardwareProfile(
            DeviceType: "Nyquist1",
            Model: "DAQiFi Nyquist 1",
            ProductUrl: "https://daqifi.com/products/nyquist-1/",
            AnalogInputs: new AnalogInputSpec(
                ChannelCount: 16,
                ResolutionBits: 12,
                Range: "0 to 5 V",
                AdcPart: null,
                SimultaneousSampling: false,
                MaxSampleRateHz: 19250),
            AnalogOutputs: new AnalogOutputSpec(
                ChannelCount: 0,
                Range: null,
                DacPart: null,
                DrivableFromThisServer: false,
                Note: "This board has no analog outputs; set_analog_output is refused."),
            DigitalIoChannelCount: 16,
            Notes: "19,250 Hz is the ceiling with one analog channel enabled; it falls as more are "
                + "enabled, and set_sample_rate rejects a request above whatever the current "
                + "selection allows rather than lowering it."),

        new HardwareProfile(
            DeviceType: "Nyquist2",
            Model: "DAQiFi Nyquist 2",
            ProductUrl: "https://daqifi.com/products/nyquist-2/",
            AnalogInputs: new AnalogInputSpec(
                ChannelCount: 16,
                ResolutionBits: 24,
                Range: "0 to 5 V",
                AdcPart: "AD7173",
                SimultaneousSampling: false,
                MaxSampleRateHz: null),
            AnalogOutputs: new AnalogOutputSpec(
                ChannelCount: 8,
                Range: null,
                DacPart: "DAC7718",
                DrivableFromThisServer: false,
                Note: "The hardware has 8 DAC channels, but this server does not drive them yet: "
                    + "set_analog_output is Nyquist 3 only and refuses this board."),
            DigitalIoChannelCount: null,
            Notes: "The 24-bit ADC is the difference from Nyquist 1 at the same count and range. "
                + "No published single-channel rate ceiling; read the one the device enforces by "
                + "the rate set_sample_rate accepts."),

        new HardwareProfile(
            DeviceType: "Nyquist3",
            Model: "DAQiFi Nyquist 3",
            ProductUrl: "https://daqifi.com/products/nyquist-3/",
            AnalogInputs: new AnalogInputSpec(
                ChannelCount: 8,
                ResolutionBits: 18,
                Range: "-10 to +10 V",
                AdcPart: "AD7609",
                SimultaneousSampling: true,
                MaxSampleRateHz: null),
            AnalogOutputs: new AnalogOutputSpec(
                ChannelCount: 8,
                Range: null,
                DacPart: "DAC7718",
                DrivableFromThisServer: true,
                Note: "The only board whose analog outputs this server drives. Use "
                    + "list_analog_outputs for the range each channel accepts — it comes from the "
                    + "device's own capability document — and set_analog_output to drive one."),
            DigitalIoChannelCount: null,
            Notes: "The only board that samples its analog inputs simultaneously rather than "
                + "multiplexed, so values in one row share an instant, and the only one that "
                + "accepts a bipolar input."),
    ];

    /// <summary>
    /// The profile for <paramref name="deviceType"/>, or null if this server has no
    /// specifications for it.
    /// </summary>
    /// <param name="deviceType">
    /// A board name, matched leniently: the <c>deviceType</c> discovery reports
    /// (<c>Nyquist1</c>) as well as <c>nyquist 1</c>, <c>nyquist-1</c> and <c>NYQUIST1</c> all
    /// find the same board. <c>Unknown</c>, which discovery reports for a device whose type it
    /// could not establish, matches nothing.
    /// </param>
    /// <remarks>
    /// Lenient on purpose. The name reaches this method through a model, which will type the
    /// board the way the product page spells it as readily as the way discovery reports it, and
    /// rejecting "Nyquist 3" for its space would send the agent away empty-handed from a board
    /// this server does describe.
    /// </remarks>
    public static HardwareProfile? Find(string? deviceType)
    {
        if (string.IsNullOrWhiteSpace(deviceType))
        {
            return null;
        }

        var wanted = Normalize(deviceType);
        foreach (var profile in All)
        {
            if (Normalize(profile.DeviceType) == wanted)
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="profile"/> as the JSON an MCP resource read returns.
    /// </summary>
    /// <remarks>
    /// Serialized with the same options as a tool result, so a field an agent saw in
    /// <c>get_hardware_info</c> is spelled identically in the resource and vice versa.
    /// </remarks>
    public static string ToJson(HardwareProfile profile) =>
        JsonSerializer.Serialize(profile, ToolRegistration.SerializerOptions);

    /// <summary>
    /// Reduces a board name to a comparable form: lowercase, with the separators that only ever
    /// vary by taste removed, so <c>Nyquist 1</c>, <c>nyquist-1</c> and <c>Nyquist1</c> agree.
    /// </summary>
    private static string Normalize(string deviceType)
    {
        var buffer = new char[deviceType.Length];
        var length = 0;
        foreach (var c in deviceType)
        {
            if (c is ' ' or '-' or '_')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer, 0, length);
    }
}

/// <summary>
/// What is published about one Nyquist board. See <see cref="HardwareCatalog"/> for what this
/// data is and is not.
/// </summary>
/// <param name="DeviceType">
/// The board as discovery reports it in <c>deviceType</c>, e.g. <c>Nyquist1</c>.
/// </param>
/// <param name="Model">The product name, e.g. <c>DAQiFi Nyquist 1</c>.</param>
/// <param name="ProductUrl">The product page this data is published on.</param>
/// <param name="AnalogInputs">What the measured analog inputs are.</param>
/// <param name="AnalogOutputs">What the driven analog outputs are, if the board has any.</param>
/// <param name="DigitalIoChannelCount">
/// Digital I/O channels, or null where no count is published for the board — call
/// <c>list_channels</c> on a connected device instead of assuming.
/// </param>
/// <param name="Notes">What an agent would otherwise get wrong about this board.</param>
public sealed record HardwareProfile(
    string DeviceType,
    string Model,
    string ProductUrl,
    AnalogInputSpec AnalogInputs,
    AnalogOutputSpec AnalogOutputs,
    int? DigitalIoChannelCount,
    string Notes);

/// <summary>The analog inputs of a board: what it measures, how finely, and how fast.</summary>
/// <param name="ChannelCount">How many analog input channels the board has.</param>
/// <param name="ResolutionBits">ADC resolution in bits, e.g. 12, 18 or 24.</param>
/// <param name="Range">The input range as published, e.g. <c>0 to 5 V</c>.</param>
/// <param name="AdcPart">The ADC part number, where it is published; otherwise null.</param>
/// <param name="SimultaneousSampling">
/// True when every channel is sampled at the same instant, false when they are multiplexed, so
/// values in one captured row are from slightly different instants.
/// </param>
/// <param name="MaxSampleRateHz">
/// The published ceiling with a single analog channel enabled, or null where none is published.
/// Enabling more channels lowers it; the device enforces the real limit either way.
/// </param>
public sealed record AnalogInputSpec(
    int ChannelCount,
    int ResolutionBits,
    string Range,
    string? AdcPart,
    bool SimultaneousSampling,
    int? MaxSampleRateHz);

/// <summary>
/// The analog outputs of a board, and whether this server can drive them.
/// </summary>
/// <param name="ChannelCount">
/// How many DAC channels the hardware has — which is not the same as how many this server will
/// drive; see <paramref name="DrivableFromThisServer"/>.
/// </param>
/// <param name="Range">
/// The output range as published, or null where it is not. <c>list_analog_outputs</c> reports the
/// range a connected device declares, which is the authority.
/// </param>
/// <param name="DacPart">The DAC part number, where it is published; otherwise null.</param>
/// <param name="DrivableFromThisServer">
/// Whether <c>set_analog_output</c> accepts this board. False both for a board with no DACs and
/// for one whose DACs this server has not implemented yet, which is why
/// <paramref name="Note"/> says which.
/// </param>
/// <param name="Note">Why the outputs are or are not drivable, in a sentence.</param>
public sealed record AnalogOutputSpec(
    int ChannelCount,
    string? Range,
    string? DacPart,
    bool DrivableFromThisServer,
    string? Note);
