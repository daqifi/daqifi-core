using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// The shape every public event-args class shares (issue #1049): sealed, and stamping wall-clock
/// time as a <see cref="DateTimeOffset"/>.
/// </summary>
public class EventArgsShapeTests
{
    private static readonly Type[] PublicEventArgs =
        [.. typeof(DaqifiDevice).Assembly.GetExportedTypes().Where(type => type.IsSubclassOf(typeof(EventArgs)))];

    [Fact]
    public void EveryPublicEventArgsClass_IsSealed()
    {
        Assert.NotEmpty(PublicEventArgs);
        Assert.All(PublicEventArgs, type => Assert.True(type.IsSealed, type.FullName));
    }

    [Fact]
    public void OnlySampleTime_IsADateTime()
    {
        // TimestampGapEventArgs.Timestamp is a position on the stream's own timeline, so it has the
        // type every sample timestamp has. Everything else says when something was observed.
        var dateTimes = PublicEventArgs
            .SelectMany(type => type.GetProperties())
            .Where(property => property.PropertyType == typeof(DateTime))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}");

        Assert.Equal([$"{nameof(TimestampGapEventArgs)}.{nameof(TimestampGapEventArgs.Timestamp)}"], dateTimes);
    }
}
