using Daqifi.Core.Firmware.Winc;
using Microsoft.Extensions.Logging;

namespace Daqifi.Core.Tests.Firmware.Winc;

/// <summary>
/// Covers the flash-level read sequences and the two <see cref="IWincFlasher"/> implementations.
/// </summary>
public class WincFlasherTests
{
    private const uint TransferDoneRegister = 0x10218;
    private const uint DummyRegister = 0x1084;
    private const uint ShareMemoryBase = 0xD0000;

    /// <summary>
    /// Builds a fake whose transfer-done register always reads 1, so flash sequences complete.
    /// </summary>
    private static FakeWincSerialPort CreateReadyPort()
    {
        var port = new FakeWincSerialPort();
        port.Registers[TransferDoneRegister] = 1;
        return port;
    }

    private static WincFlashReader CreateReader(FakeWincSerialPort port)
        => new(new WincSerialBridgeClient(port, TimeSpan.FromSeconds(1)));

    [Theory]
    [InlineData(0x001003A0u, true)]  // bench-observed: halted in download mode
    [InlineData(0x00150000u, true)]  // firmware running
    [InlineData(0x0015FFFFu, true)]
    [InlineData(0x00000000u, false)] // nothing there
    [InlineData(0xFFFFFFFFu, false)] // floating bus
    [InlineData(0x00200000u, false)] // some other part
    public void IsKnownWincChipId_RecognizesOnlyTheWinc1500Families(uint chipId, bool expected)
    {
        Assert.Equal(expected, WincFlashReader.IsKnownWincChipId(chipId));
    }

    [Fact]
    public void ReadChipId_ReadsTheIdentityRegister()
    {
        var port = CreateReadyPort();
        port.Registers[WincFlashReader.ChipIdRegister] = 0x001003A0;

        Assert.Equal(0x001003A0u, CreateReader(port).ReadChipId());
    }

    [Fact]
    public void ReadFlash_ChunksBelowTheSizeThatWedgesTheDevice()
    {
        // A 5 KB read must never issue a single block request at or above 2048 bytes, because the
        // device's read loop would never terminate. This is the property that matters most here.
        var port = CreateReadyPort();
        port.Blocks[ShareMemoryBase] = new byte[WincBridgeProtocol.MaxReadBlockSize];

        CreateReader(port).ReadFlash(0, 5000);

        var blockReadSizes = port.ReceivedHeaders
            .Where(h => h[0] == (byte)WincBridgeProtocol.Command.ReadBlock)
            .Select(h => (h[3] << 8) | h[2])
            .ToList();

        Assert.NotEmpty(blockReadSizes);
        Assert.All(blockReadSizes, size => Assert.True(size <= WincBridgeProtocol.MaxReadBlockSize));
    }

    [Fact]
    public void ReadFlash_ReturnsExactlyTheRequestedLength()
    {
        var port = CreateReadyPort();
        port.Blocks[ShareMemoryBase] = Enumerable.Range(0, WincBridgeProtocol.MaxReadBlockSize)
            .Select(i => (byte)(i & 0xFF))
            .ToArray();

        var data = CreateReader(port).ReadFlash(0, 3000);

        Assert.Equal(3000, data.Length);
    }

    [Fact]
    public void ReadFlash_IssuesTheFastReadCommandWithTheAddressInTheControllerWord()
    {
        // Mirrors the WINC host driver's load-to-shared-memory sequence: opcode 0x0B in the low
        // byte, then the 24-bit flash address ascending.
        var port = CreateReadyPort();
        port.Blocks[ShareMemoryBase] = new byte[16];

        CreateReader(port).ReadFlash(0x123456, 16);

        var buffer1Write = port.ReceivedHeaders.First(h =>
            h[0] == (byte)WincBridgeProtocol.Command.WriteRegister &&
            (((uint)h[7] << 24) | ((uint)h[6] << 16) | ((uint)h[5] << 8) | h[4]) == 0x1020C);

        var commandWord = ((uint)buffer1Write[11] << 24) | ((uint)buffer1Write[10] << 16)
                        | ((uint)buffer1Write[9] << 8) | buffer1Write[8];

        Assert.Equal(0x0Bu, commandWord & 0xFF);
        Assert.Equal(0x12u, (commandWord >> 8) & 0xFF);
        Assert.Equal(0x34u, (commandWord >> 16) & 0xFF);
        Assert.Equal(0x56u, (commandWord >> 24) & 0xFF);
    }

    [Fact]
    public void ReadFlashJedecId_ReturnsTheControllerResult()
    {
        var port = CreateReadyPort();
        port.Registers[DummyRegister] = 0x00C22018;

        Assert.Equal(0x00C22018u, CreateReader(port).ReadFlashJedecId());
    }

    [Fact]
    public void ReadFlash_ThrowsWhenTheControllerNeverReportsDone()
    {
        // The firmware's own poll loop is unbounded; ours must not be, or a WINC that stops
        // answering would hang the host indefinitely.
        var port = new FakeWincSerialPort();
        port.Registers[TransferDoneRegister] = 0;

        var reader = new WincFlashReader(
            new WincSerialBridgeClient(port, TimeSpan.FromSeconds(1)), transferPollLimit: 3);

        var ex = Assert.Throws<TimeoutException>(() => reader.ReadFlash(0, 16));

        Assert.Contains("transfer-done", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReadFlash_RejectsNonPositiveLengths(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateReader(CreateReadyPort()).ReadFlash(0, length));
    }

    [Theory]
    [InlineData(0xFFFF00u, 0x200)]   // straddles the top of the 24-bit space
    [InlineData(0xFFFFFFu, 2)]       // one byte past the last address
    [InlineData(0xFFFFFFu, int.MaxValue)]
    public void ReadFlash_RejectsSpansThatWouldWrapTheAddress(uint offset, int length)
    {
        // Without the bounds check, offset + read wraps past 32 bits and the chunk silently
        // targets the wrong flash address — returning plausible data with no error at all.
        var port = CreateReadyPort();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateReader(port).ReadFlash(offset, length));

        Assert.Equal("length", ex.ParamName);
        Assert.Empty(port.ReceivedHeaders);
    }

    [Fact]
    public void ReadFlash_AcceptsASpanEndingExactlyAtTheLastAddress()
    {
        // Boundary: the final byte is addressable and must not be rejected.
        var port = CreateReadyPort();
        port.Blocks[ShareMemoryBase] = new byte[16];

        var data = CreateReader(port).ReadFlash(WincFlashReader.MaxFlashAddress - 15, 16);

        Assert.Equal(16, data.Length);
    }

    [Fact]
    public void ReadFlash_ObservesCancellation()
    {
        var port = CreateReadyPort();
        port.Blocks[ShareMemoryBase] = new byte[WincBridgeProtocol.MaxReadBlockSize];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CreateReader(port).ReadFlash(0, 5000, cts.Token));
    }

    // ---- WincModuleInspector -------------------------------------------------

    [Fact]
    public async Task Inspector_ReadIdentityAsync_HandshakesNegotiatesBaudThenReadsIdentity()
    {
        var port = CreateReadyPort();
        port.Registers[WincFlashReader.ChipIdRegister] = 0x001003A0;
        port.Registers[DummyRegister] = 0x00C22018;

        var flasher = new WincModuleInspector((_, _) => port, baudSettleDelay: TimeSpan.Zero);

        var identity = await flasher.ReadIdentityAsync("COM1");

        Assert.Equal(0x001003A0u, identity.ChipId);
        Assert.Equal(0x00C22018u, identity.FlashJedecId);
        Assert.True(identity.IsRecognizedWinc);
        Assert.Equal(WincModuleInspector.FastBaudRate, identity.NegotiatedBaudRate);
        Assert.Contains(WincModuleInspector.FastBaudRate, port.BaudRateHistory);
    }

    [Fact]
    public async Task Inspector_ReadIdentityAsync_ReportsAnUnrecognizedChip()
    {
        // Bridge up but WINC not answering is a distinct, actionable condition from a dead port.
        var port = CreateReadyPort();
        port.Registers[WincFlashReader.ChipIdRegister] = 0xFFFFFFFF;

        var flasher = new WincModuleInspector((_, _) => port, baudSettleDelay: TimeSpan.Zero);

        var identity = await flasher.ReadIdentityAsync("COM1");

        Assert.False(identity.IsRecognizedWinc);
    }

    [Fact]
    public async Task Inspector_ReadIdentityAsync_ExplainsWhenNoBridgeIsListening()
    {
        var port = new FakeWincSerialPort { SuppressIdentityResponse = true };
        var flasher = new WincModuleInspector((_, _) => port, baudSettleDelay: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<IOException>(() => flasher.ReadIdentityAsync("COM1"));

        Assert.Contains("bridge", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(port.WasDisposed);
    }

    [Fact]
    public async Task Inspector_AbandonsAnOpenThatHangs_RatherThanBlockingForever()
    {
        // SerialPort.Open takes no cancellation token and can block indefinitely on a wedged or
        // half-enumerated USB CDC device. The only way to stay responsive is a hard deadline.
        var port = new HangingOpenPort();
        var inspector = new WincModuleInspector(
            (_, _) => port,
            baudSettleDelay: TimeSpan.Zero,
            openTimeout: TimeSpan.FromMilliseconds(150));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => inspector.ReadIdentityAsync("COM1"));
        sw.Stop();

        Assert.Contains("did not complete", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}, should have bailed at the deadline");

        // The abandoned open still owns the handle, so this side must NOT have disposed it.
        Assert.False(port.DisposedWhileOpenStillBlocked);

        port.ReleaseOpen();
    }

    [Fact]
    public async Task Inspector_ObservesTheFaultOfAnAbandonedOpen()
    {
        // The abandoned open is no longer awaited by anyone, so if it later faults nothing would
        // observe the exception - a silently swallowed background failure (#377/#394).
        //
        // Asserted through the logger rather than TaskScheduler.UnobservedTaskException plus a
        // forced GC. That route depends on collector and finalizer timing — nondeterminism that
        // makes a flaky CI gate — and the event is process-global, coupling this test to every
        // other task in a parallel run.
        //
        // Reading Task.Exception is what marks a fault observed, and production already reads it
        // to hand to the logger. So a logger that captures the exception proves the read happened,
        // using a seam that exists for production reasons rather than a test-only hook.
        var logger = new CapturingLogger();
        var port = new FaultingAfterDelayPort(TimeSpan.FromMilliseconds(150));
        var inspector = new WincModuleInspector(
            (_, _) => port,
            logger,
            baudSettleDelay: TimeSpan.Zero,
            openTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => inspector.ReadIdentityAsync("COM1"));

        var observed = await logger.FirstException.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsType<AggregateException>(observed);
        Assert.IsType<InvalidOperationException>(((AggregateException)observed).InnerException);

        // The continuation still owns disposal of the port it took over.
        await port.Disposed.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Inspector_DisposesTheAbandonedPort_EvenWhenObservingTheFaultThrows()
    {
        // Releasing the handle is the whole reason the abandonment continuation exists. If a
        // throwing logger could skip past the disposal, the abandoned port would leak and break
        // every later open until the process exits — strictly worse than the fault being reported.
        var port = new FaultingAfterDelayPort(TimeSpan.FromMilliseconds(150));
        var inspector = new WincModuleInspector(
            (_, _) => port,
            new ThrowingLogger(),
            baudSettleDelay: TimeSpan.Zero,
            openTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => inspector.ReadIdentityAsync("COM1"));

        // Disposal must still happen despite the observation throwing.
        await port.Disposed.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Inspector_ObservesCancellationDuringAHangingOpen()
    {
        var port = new HangingOpenPort();
        var inspector = new WincModuleInspector(
            (_, _) => port,
            baudSettleDelay: TimeSpan.Zero,
            openTimeout: TimeSpan.FromMinutes(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspector.ReadIdentityAsync("COM1", cts.Token));

        port.ReleaseOpen();
    }

    [Fact]
    public async Task Inspector_ReadIdentityAsync_RejectsAnEmptyPortName()
    {
        var flasher = new WincModuleInspector((_, _) => CreateReadyPort());

        await Assert.ThrowsAsync<ArgumentException>(() => flasher.ReadIdentityAsync("  "));
    }

    // ---- WincFlashToolLocator -------------------------------------------

    [Fact]
    public void Locator_IsAvailable_WhenTheFirmwarePathIsTheToolItself()
    {
        var toolPath = Path.Combine(Path.GetTempPath(), $"winc_flash_tool_{Guid.NewGuid():N}.cmd");
        File.WriteAllText(toolPath, "@echo off");

        try
        {
            var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

            Assert.True(locator.IsAvailable(toolPath));
        }
        finally
        {
            File.Delete(toolPath);
        }
    }

    [Fact]
    public void Locator_IsAvailable_FindsTheToolBeneathADirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winc_{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "winc");
        Directory.CreateDirectory(nested);
        var toolPath = Path.Combine(nested, "winc_flash_tool.cmd");
        File.WriteAllText(toolPath, "@echo off");

        try
        {
            var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

            Assert.True(locator.IsAvailable(root));
            Assert.True(locator.TryResolveToolPath(root, out var resolved));
            Assert.Equal(toolPath, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Locator_IsNotAvailable_WhenTheToolIsMissing()
    {
        // This is the Linux/macOS case that motivates issue #271 — the answer must be a clean
        // "no", not an exception mid-update.
        var root = Path.Combine(Path.GetTempPath(), $"winc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

            Assert.False(locator.IsAvailable(root));
            Assert.False(locator.TryResolveToolPath(root, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Locator_TryResolve_PropagatesAnUnreadableTree_RatherThanReportingNotFound()
    {
        // "Could not locate the tool - WiFi flashing is Windows-only" is genuinely misleading when
        // the tool is sitting right there behind a permissions problem, so this case must surface.
        if (OperatingSystem.IsWindows())
        {
            return; // chmod semantics differ; the behavior under test is the catch removal itself.
        }

        var root = Path.Combine(Path.GetTempPath(), $"winc_{Guid.NewGuid():N}");
        var locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            File.SetUnixFileMode(locked, UnixFileMode.None);

            var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

            Assert.ThrowsAny<Exception>(() => locator.TryResolveToolPath(root, out _));

            // IsAvailable stays total: a probe answers yes/no and must not throw.
            Assert.False(locator.IsAvailable(root));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Locator_IsNotAvailable_ForAPathThatDoesNotExist()
    {
        var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

        Assert.False(locator.IsAvailable(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}")));
    }

    /// <summary>
    /// A port whose <see cref="Open"/> blocks until released, standing in for the real
    /// <c>SerialPort.Open()</c> hang on a wedged or half-enumerated USB CDC device.
    /// </summary>
    private sealed class HangingOpenPort : IWincSerialPort
    {
        private readonly ManualResetEventSlim _release = new(false);

        /// <summary>True if something disposed the port while the open was still blocked.</summary>
        internal bool DisposedWhileOpenStillBlocked { get; private set; }

        public bool IsOpen { get; private set; }
        public int BaudRate { get; set; } = 115200;

        public void Open()
        {
            _release.Wait();
            IsOpen = true;
        }

        internal void ReleaseOpen() => _release.Set();

        public void Close() => IsOpen = false;
        public void DiscardInBuffer() { }
        public void Write(byte[] buffer, int offset, int count) { }

        public void ReadExactly(byte[] buffer, int offset, int count, TimeSpan timeout)
            => throw new TimeoutException("Port never opened.");

        public void Dispose()
        {
            if (!_release.IsSet)
            {
                DisposedWhileOpenStillBlocked = true;
            }

            _release.Set();
        }
    }

    [Theory]
    [InlineData("\0invalid")]                       // embedded NUL - ArgumentException, not IO
    [InlineData("   ")]
    [InlineData("")]
    public void Locator_IsAvailable_IsTotal_EvenForMalformedPaths(string path)
    {
        // IsAvailable is documented as never throwing. A narrow IO-only catch would let an
        // ArgumentException from a malformed path escape a probe whose entire purpose is to be
        // safe to call with anything.
        var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

        var ex = Record.Exception(() => locator.IsAvailable(path));

        Assert.Null(ex);
        Assert.False(locator.IsAvailable(path));
    }

    [Fact]
    public void Locator_IsAvailable_IsTotal_ForAnAbsurdlyLongPath()
    {
        var locator = new WincFlashToolLocator("winc_flash_tool.cmd");
        var longPath = "/" + new string('x', 40_000);

        Assert.Null(Record.Exception(() => locator.IsAvailable(longPath)));
    }

    /// <summary>Captures logged exceptions, exposing the first one as an awaitable.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly TaskCompletionSource<Exception> _firstException =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<Exception> FirstException => _firstException.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                _firstException.TrySetResult(exception);
            }
        }
    }

    /// <summary>A logger that throws, standing in for a misbehaving logging pipeline.</summary>
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logger-boom");
    }

    /// <summary>
    /// A port whose <see cref="Open"/> blocks briefly and then throws, standing in for an open that
    /// is abandoned on the timeout and only fails afterwards.
    /// </summary>
    private sealed class FaultingAfterDelayPort(TimeSpan delay) : IWincSerialPort
    {
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the port is disposed, so tests can await it deterministically.</summary>
        internal Task Disposed => _disposed.Task;

        public bool IsOpen => false;
        public int BaudRate { get; set; } = 115200;

        public void Open()
        {
            Thread.Sleep(delay);
            throw new InvalidOperationException("abandoned-open-fault");
        }

        public void Close() { }
        public void DiscardInBuffer() { }
        public void Write(byte[] buffer, int offset, int count) { }

        public void ReadExactly(byte[] buffer, int offset, int count, TimeSpan timeout)
            => throw new TimeoutException("Port never opened.");

        public void Dispose() => _disposed.TrySetResult();
    }

    [Fact]
    public void Locator_RejectsAnEmptyToolName()
    {
        Assert.Throws<ArgumentException>(() => new WincFlashToolLocator("  "));
    }


}
