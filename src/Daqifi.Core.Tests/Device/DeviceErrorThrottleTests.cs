using Daqifi.Core.Device;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #378: the error surface has to stay useful under a systematic failure that repeats at the
/// frame rate. These pin the documented policy — first occurrence always through, repeats collapsed
/// per (source, exception type) with the collapsed count reported on the next raise.
/// </summary>
public class DeviceErrorThrottleTests
{
    private static readonly TimeSpan ShortInterval = TimeSpan.FromMilliseconds(150);

    [Fact]
    public void TheFirstOccurrence_IsAlwaysRaised()
    {
        var throttle = new DeviceErrorThrottle(ShortInterval);

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.StreamDecode, new InvalidOperationException(), out var suppressed));
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void RepeatsWithinTheInterval_AreCollapsed()
    {
        var throttle = new DeviceErrorThrottle(TimeSpan.FromMinutes(1));

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.StreamDecode, new InvalidOperationException(), out _));

        for (var i = 0; i < 1000; i++)
        {
            Assert.False(throttle.ShouldRaise(DeviceErrorSource.StreamDecode, new InvalidOperationException(), out _));
        }
    }

    [Fact]
    public void TheNextRaiseAfterTheInterval_ReportsHowManyWereCollapsed()
    {
        var throttle = new DeviceErrorThrottle(ShortInterval);

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, new IOException(), out _));

        const int collapsed = 25;
        for (var i = 0; i < collapsed; i++)
        {
            Assert.False(throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, new IOException(), out _));
        }

        Thread.Sleep(ShortInterval + ShortInterval);

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, new IOException(), out var suppressed));
        Assert.Equal(collapsed, suppressed);

        // The count is consumed by the raise that reports it, not carried forward.
        Thread.Sleep(ShortInterval + ShortInterval);
        Assert.True(throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, new IOException(), out var afterReport));
        Assert.Equal(0, afterReport);
    }

    [Fact]
    public void ADifferentExceptionType_IsNotDelayedBehindAnOngoingStorm()
    {
        // The whole point of bucketing: a new failure mode appearing during a storm of another one
        // is exactly the thing an operator needs to see, and it must not wait for a window to open.
        var throttle = new DeviceErrorThrottle(TimeSpan.FromMinutes(1));

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.StreamDecode, new InvalidOperationException(), out _));
        Assert.False(throttle.ShouldRaise(DeviceErrorSource.StreamDecode, new InvalidOperationException(), out _));

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.StreamDecode, new IndexOutOfRangeException(), out _));
    }

    [Fact]
    public void ADifferentSource_IsNotDelayedBehindAnOngoingStorm()
    {
        var throttle = new DeviceErrorThrottle(TimeSpan.FromMinutes(1));

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.StreamDecode, new IOException(), out _));
        Assert.False(throttle.ShouldRaise(DeviceErrorSource.StreamDecode, new IOException(), out _));

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, new IOException(), out _));
    }

    [Fact]
    public void AZeroInterval_DisablesCollapsingEntirely()
    {
        var throttle = new DeviceErrorThrottle(TimeSpan.Zero);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(throttle.ShouldRaise(DeviceErrorSource.StreamDecode, new IOException(), out var suppressed));
            Assert.Equal(0, suppressed);
        }
    }

    [Fact]
    public void Reset_LetsTheNextOccurrenceThroughImmediately()
    {
        var throttle = new DeviceErrorThrottle(TimeSpan.FromMinutes(1));

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, new IOException(), out _));
        Assert.False(throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, new IOException(), out _));

        throttle.Reset();

        Assert.True(throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, new IOException(), out _));
    }

    [Fact]
    public void ManyDistinctFailureTypes_DoNotGrowTheBucketTableWithoutBound()
    {
        var throttle = new DeviceErrorThrottle(TimeSpan.FromMinutes(1));

        // More distinct types than the cap, driven through twice: past the cap they share the
        // overflow bucket, so the second pass must be collapsed rather than raised again.
        var errors = BuildDistinctErrors();

        foreach (var error in errors)
        {
            throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, error, out _);
        }

        var raisedOnSecondPass = errors.Count(e => throttle.ShouldRaise(DeviceErrorSource.MessageConsumer, e, out _));
        Assert.Equal(0, raisedOnSecondPass);
    }

    /// <summary>
    /// Builds a list of exceptions with distinct runtime types, so each would claim its own bucket.
    /// </summary>
    private static List<Exception> BuildDistinctErrors()
    {
        var errors = new List<Exception>
        {
            new IOException(),
            new EndOfStreamException(),
            new FileNotFoundException(),
            new DirectoryNotFoundException(),
            new PathTooLongException(),
            new FileLoadException(),
            new InvalidOperationException(),
            new IndexOutOfRangeException(),
            new FormatException(),
            new NotSupportedException(),
            new NotImplementedException(),
            new TimeoutException(),
            new ArgumentException(),
            new ArgumentNullException(),
            new ArgumentOutOfRangeException(),
            new OverflowException(),
            new ObjectDisposedException("stream"),
            new NullReferenceException(),
            new InvalidCastException(),
            new RankException(),
            new ArithmeticException(),
            new DivideByZeroException(),
            new KeyNotFoundException(),
            new PlatformNotSupportedException(),
            new UnauthorizedAccessException(),
            new ApplicationException(),
            new SystemException(),
            new MissingFieldException(),
            new MissingMethodException(),
            new MissingMemberException(),
            new BadImageFormatException(),
            new TypeLoadException(),
            new DataMisalignedException(),
            new InsufficientMemoryException(),
            new OutOfMemoryException(),
            new AggregateException(),
            new OperationCanceledException(),
            new ArrayTypeMismatchException(),
            new MethodAccessException(),
            new FieldAccessException(),
        };

        Assert.True(errors.Count > DeviceErrorThrottle.MaxTrackedBuckets, "the fixture must exceed the cap");
        return errors;
    }
}
