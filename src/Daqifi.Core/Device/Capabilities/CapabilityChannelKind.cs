namespace Daqifi.Core.Device.Capabilities;

/// <summary>
/// The <c>kind</c> of an entry in the capability document's flat <c>channels[]</c> array.
/// </summary>
/// <remarks>
/// The firmware schema is deliberately open-ended: new <c>kind</c> values are an additive change
/// that does not bump the schema version, and clients are required to ignore ones they do not
/// recognize. Unrecognized values therefore map to <see cref="Unknown"/> rather than failing the
/// parse.
/// </remarks>
public enum CapabilityChannelKind
{
    /// <summary>A <c>kind</c> this version of daqifi-core does not recognize.</summary>
    Unknown = 0,

    /// <summary><c>"analog-input"</c>.</summary>
    AnalogInput,

    /// <summary><c>"analog-output"</c>.</summary>
    AnalogOutput,

    /// <summary><c>"digital-io"</c>.</summary>
    DigitalIo
}
