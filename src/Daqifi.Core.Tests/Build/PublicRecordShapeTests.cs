using System;
using System.Linq;
using System.Reflection;
using Daqifi.Core.Channel;
using Daqifi.Core.Export;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Pins ADR 0002's 2.0 amendment (#1036): a public record is nominal, because appending a
/// parameter to a positional one removes the constructor and <c>Deconstruct</c> consumers
/// compiled against.
/// </summary>
public class PublicRecordShapeTests
{
    /// <summary>
    /// The records whose parts are the type's definition, so no later release appends to them.
    /// </summary>
    private static readonly Type[] ClosedShapes =
    [
        typeof(ChannelDescriptor),
        typeof(FirmwareVersion),
        typeof(LiveSample),
        typeof(SampleRow),
    ];

    [Fact]
    public void PublicRecords_AreNominal_UnlessTheirShapeIsClosed()
    {
        // A Deconstruct method is what the compiler emits for a positional record and for nothing
        // else in this library.
        var positional = typeof(FirmwareVersion).Assembly.GetExportedTypes()
            .Where(type => type.GetMethod(
                "Deconstruct",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        Assert.Equal(
            ClosedShapes.OrderBy(type => type.FullName, StringComparer.Ordinal),
            positional);
    }
}
