using Daqifi.Core.Device.Discovery;
using System.Runtime.InteropServices;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Tests for the Linux sysfs USB descriptor provider, driven against a fixture tty class tree
/// rather than the real <c>/sys/class/tty</c> (slice 3 of #464).
/// </summary>
/// <remarks>
/// <para>
/// This provider had no tests at all, and it is not incidental code: CI runs on
/// <c>ubuntu-latest</c>, so it is the descriptor provider that actually executes there, and it is
/// what pre-filters ports for every Linux consumer of <c>SerialDeviceFinder</c>. A wrong answer is
/// not a crash — a DAQiFi board silently stops being discovered, or every unrelated COM port gets
/// probed again.
/// </para>
/// <para>
/// Most fixtures build <c>&lt;root&gt;/&lt;tty&gt;/device</c> as a real directory, because what
/// those tests are about is the walk and the parsing, and the provider treats a real directory as
/// the plain case. The four tests that are specifically about the link — the realistic sysfs shape,
/// the physical-vs-logical parent chain, and the two depth bounds — need a real directory symlink;
/// they are grouped together and noted as such.
/// </para>
/// </remarks>
public class LinuxUsbPortDescriptorProviderTests : IDisposable
{
    /// <summary>DAQiFi's USB vendor ID as sysfs writes it: lowercase hex, newline-terminated.</summary>
    private const string DaqifiVendorText = "04d8\n";

    /// <summary>DAQiFi's CDC-mode product ID in the same form.</summary>
    private const string DaqifiProductText = "f794\n";

    /// <summary>
    /// How many device-tree levels the provider examines, restated as a literal rather than read
    /// from <see cref="LinuxUsbPortDescriptorProvider.MaxDeviceTreeLevels"/>. Building the fixture
    /// from the constant would make the boundary tests move with any change to it, which is the
    /// one thing they exist to catch.
    /// </summary>
    private const int ExpectedDeviceTreeLevels = 8;

    private readonly string _root;

    public LinuxUsbPortDescriptorProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "daqifi-sysfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    #region The happy path

    [Fact]
    public void Resolve_IdsOnTheNodeItself_IsFoundWithoutWalkingUp()
    {
        WriteIds(MountTty("ttyUSB0"), DaqifiVendorText, DaqifiProductText);

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyUSB0"));
    }

    [Fact]
    public void Resolve_IdsOnTheNodeAbove_AreWalkedUpTo()
    {
        // The usual shape: the tty hangs off an interface node, and only the USB device node above
        // it carries idVendor/idProduct.
        var node = MountTty("ttyACM0");
        WriteIds(Directory.GetParent(node)!.FullName, DaqifiVendorText, DaqifiProductText);

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyACM0"));
    }

    [Fact]
    public void Resolve_PortNameWithoutADirectory_IsTreatedAsTheBaseName()
    {
        // SerialPort.GetPortNames() yields full device paths on Linux, but nothing in the contract
        // requires one — a caller passing the bare tty name must resolve the same port.
        WriteIds(MountTty("ttyACM2"), DaqifiVendorText, DaqifiProductText);

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("ttyACM2"));
    }

    [Fact]
    public void Resolve_TwoPorts_AnswerIndependently()
    {
        WriteIds(MountTty("ttyACM0"), DaqifiVendorText, DaqifiProductText);
        WriteIds(MountTty("ttyUSB0"), "1a86\n", "7523\n");

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyACM0"));
        Assert.Equal(new UsbPortDescriptor(0x1A86, 0x7523), Resolve("/dev/ttyUSB0"));
    }

    #endregion

    #region Ports with no descriptor to give

    [Fact]
    public void Resolve_PortWithNoSysfsEntry_ReturnsNull()
    {
        Assert.Null(Resolve("/dev/ttyACM9"));
    }

    [Fact]
    public void Resolve_TtyClassRootDoesNotExist_ReturnsNull()
    {
        var missing = Path.Combine(_root, "no-such-class-dir");

        Assert.Null(LinuxUsbPortDescriptorProvider.Resolve("/dev/ttyACM0", missing));
    }

    [Fact]
    public void Resolve_TtyEntryWithoutADeviceNode_ReturnsNull()
    {
        // A virtual tty (pty, console) has a class entry but no device node behind it.
        CreateSubdirectory(_root, "tty0");

        Assert.Null(Resolve("/dev/tty0"));
    }

    [Fact]
    public void Resolve_NonUsbSerialPort_ReturnsNull()
    {
        // A device node with no idVendor/idProduct anywhere in reach — e.g. an on-board 16550.
        MountTty("ttyS0");

        Assert.Null(Resolve("/dev/ttyS0"));
    }

    [Fact]
    public void Resolve_DeviceEntryIsAFileNotADirectory_ReturnsNull()
    {
        var ttyEntry = CreateSubdirectory(_root, "ttyACM0");
        File.WriteAllText(Path.Combine(ttyEntry, "device"), "not a directory");

        Assert.Null(Resolve("/dev/ttyACM0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/dev/")]
    [InlineData("/")]
    public void Resolve_PortNameWithNoFinalSegment_ReturnsNull(string portName)
    {
        Assert.Null(Resolve(portName));
    }

    [Fact]
    public void Resolve_NullPortName_ReturnsNullRatherThanThrowing()
    {
        // The provider is called for every name the port enumeration hands back; a null must be
        // an answer, not an exception that aborts the sweep.
        Assert.Null(Resolve(null!));
    }

    #endregion

    #region Reading the id files

    [Fact]
    public void Resolve_UppercaseHexValues_AreParsed()
    {
        WriteIds(MountTty("ttyACM0"), "04D8\n", "F794\n");

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyACM0"));
    }

    [Fact]
    public void Resolve_ValuesWithSurroundingWhitespace_AreAccepted()
    {
        WriteIds(MountTty("ttyACM0"), "  04d8\r\n", "\tf794  \n");

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyACM0"));
    }

    [Fact]
    public void Resolve_ZeroedIds_AreAnAnswerNotAMiss()
    {
        // 0000/0000 is a real (if unusual) descriptor. It must not be confused with "not found",
        // which is why the provider distinguishes null from a zero-valued descriptor.
        WriteIds(MountTty("ttyACM0"), "0000\n", "0000\n");

        Assert.Equal(new UsbPortDescriptor(0, 0), Resolve("/dev/ttyACM0"));
    }

    [Theory]
    [InlineData("zzzz\n", "f794\n")]
    [InlineData("04d8\n", "zzzz\n")]
    [InlineData("\n", "f794\n")]
    [InlineData("-1\n", "f794\n")]
    public void Resolve_UnparseableIdValue_ReturnsNullWithoutWalkingPastTheNode(
        string vendorText, string productText)
    {
        // The node that holds both files is the USB device node by definition. If its values do
        // not parse, the answer is "unknown" — continuing up would report the *hub's* IDs as the
        // port's, which is worse than no answer.
        var node = MountTty("ttyACM0");
        WriteIds(Directory.GetParent(node)!.FullName, "1d6b\n", "0002\n");
        WriteIds(node, vendorText, productText);

        Assert.Null(Resolve("/dev/ttyACM0"));
    }

    [Fact]
    public void Resolve_NodeWithOnlyAVendorId_KeepsWalkingUp()
    {
        // Both files have to be present for a node to count. A partial node is not the USB device
        // node, so the walk must continue rather than give up or answer from half a descriptor.
        var node = MountTty("ttyACM0");
        WriteIds(Directory.GetParent(node)!.FullName, DaqifiVendorText, DaqifiProductText);
        File.WriteAllText(Path.Combine(node, "idVendor"), "1a86\n");

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyACM0"));
    }

    [Fact]
    public void Resolve_NodeWithOnlyAProductId_KeepsWalkingUp()
    {
        var node = MountTty("ttyACM0");
        WriteIds(Directory.GetParent(node)!.FullName, DaqifiVendorText, DaqifiProductText);
        File.WriteAllText(Path.Combine(node, "idProduct"), "7523\n");

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyACM0"));
    }

    [Fact]
    public void Resolve_IdEntryIsADirectory_IsNotMistakenForTheValueFile()
    {
        var node = MountTty("ttyACM0");
        WriteIds(Directory.GetParent(node)!.FullName, DaqifiVendorText, DaqifiProductText);
        Directory.CreateDirectory(Path.Combine(node, "idVendor"));
        Directory.CreateDirectory(Path.Combine(node, "idProduct"));

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyACM0"));
    }

    #endregion

    #region What the device symlink is for

    // The four tests below are the ones that are actually about the link, so they create a real
    // directory symlink and let a failure to do so fail the test. That is unprivileged on Linux
    // and macOS — CI runs ubuntu-latest — and needs Developer Mode or an elevated shell on
    // Windows, which is not a platform this suite runs on. Everything else above deliberately
    // needs no symlink at all.

    [Fact]
    public void Resolve_RealSysfsShape_ReadsIdsFromTheUsbDeviceNodeAboveTheInterface()
    {
        // What the kernel actually lays out: the tty's device symlink points at the *interface*
        // node (1-1.2:1.0) under /sys/devices, and only its parent — the USB device node — carries
        // idVendor/idProduct.
        var deviceNode = CreateDeviceTree("usb1", "1-1", "1-1.2");
        WriteIds(deviceNode, DaqifiVendorText, DaqifiProductText);
        LinkTty("ttyACM0", CreateSubdirectory(deviceNode, "1-1.2:1.0"));

        var descriptor = Resolve("/dev/ttyACM0");

        Assert.NotNull(descriptor);
        Assert.Equal(0x04D8, descriptor!.VendorId);
        Assert.Equal(0xF794, descriptor.ProductId);
    }

    [Fact]
    public void Resolve_FollowsThePhysicalTargetRatherThanTheLogicalPath()
    {
        // The whole reason the provider resolves the link first. Walking the *logical* parents of
        // <root>/ttyACM0/device climbs back through the tty class directory; walking the resolved
        // target climbs the real device tree. Both chains are populated here with different IDs,
        // so a provider that forgot to resolve would answer — just with the wrong device.
        var deviceNode = CreateDeviceTree("usb1", "1-1", "1-1.2");
        WriteIds(deviceNode, DaqifiVendorText, DaqifiProductText);
        var ttyEntry = CreateSubdirectory(_root, "ttyACM0");
        WriteIds(ttyEntry, "1a86\n", "7523\n");
        Directory.CreateSymbolicLink(Path.Combine(ttyEntry, "device"), deviceNode);

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyACM0"));
    }

    [Fact]
    public void MaxDeviceTreeLevels_IsTheBoundTheseTestsAssume()
    {
        Assert.Equal(ExpectedDeviceTreeLevels, LinuxUsbPortDescriptorProvider.MaxDeviceTreeLevels);
    }

    [Fact]
    public void Resolve_IdsAtTheLastExaminedLevel_AreStillFound()
    {
        // chain[^1] is the node the tty points at (level 0) and chain[1] is seven ancestors above
        // it — the last level the provider looks at.
        var chain = CreateChain(ExpectedDeviceTreeLevels);
        WriteIds(chain[1], DaqifiVendorText, DaqifiProductText);
        LinkTty("ttyACM0", chain[^1]);

        Assert.Equal(new UsbPortDescriptor(0x04D8, 0xF794), Resolve("/dev/ttyACM0"));
    }

    [Fact]
    public void Resolve_IdsOneLevelBeyondTheBound_AreNotReached()
    {
        // chain[0] is one ancestor further up than the walk goes. Answering from it would mean the
        // bound is not doing its job; answering null is the documented behavior.
        var chain = CreateChain(ExpectedDeviceTreeLevels);
        WriteIds(chain[0], DaqifiVendorText, DaqifiProductText);
        LinkTty("ttyACM0", chain[^1]);

        Assert.Null(Resolve("/dev/ttyACM0"));
    }

    #endregion

    #region The platform gate

    [Fact]
    public void GetDescriptor_PortThatDoesNotExist_ReturnsNull()
    {
        var provider = new LinuxUsbPortDescriptorProvider();

        Assert.Null(provider.GetDescriptor("/dev/ttyDAQIFI-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void GetDescriptor_OffLinux_AnswersNullForEveryPort()
    {
        // Elsewhere /sys/class/tty either does not exist or means something else, so the gate — not
        // the filesystem — has to produce the answer. On Linux the same call is covered above.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var provider = new LinuxUsbPortDescriptorProvider();

        Assert.Null(provider.GetDescriptor("ttyACM0"));
        Assert.Null(provider.GetDescriptor("/dev/cu.usbmodem101"));
        Assert.Null(provider.GetDescriptor("COM9"));
    }

    [Fact]
    public void DefaultTtyClassRoot_IsTheKernelsTtyClassDirectory()
    {
        // Pinned because it is the one input the tests above deliberately do not use.
        Assert.Equal("/sys/class/tty", LinuxUsbPortDescriptorProvider.DefaultTtyClassRoot);
    }

    #endregion

    #region Fixture helpers

    private UsbPortDescriptor? Resolve(string portName)
        => LinuxUsbPortDescriptorProvider.Resolve(portName, _root);

    /// <summary>
    /// Creates the tty class entry for <paramref name="ttyName"/> with a real <c>device</c>
    /// directory, and returns that directory — the node the walk starts from. Its parent is the
    /// tty entry, which is where a test puts the IDs it wants found one level up.
    /// </summary>
    private string MountTty(string ttyName)
        => CreateSubdirectory(CreateSubdirectory(_root, ttyName), "device");

    /// <summary>Creates a nested device-tree path under the fixture root and returns the leaf.</summary>
    private string CreateDeviceTree(params string[] segments)
    {
        var path = Path.Combine(_root, "devices");
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Creates <paramref name="levels"/> + 1 nested directories and returns them outermost-first,
    /// so <c>[^1]</c> is the deepest node and <c>[0]</c> is <paramref name="levels"/> ancestors above it.
    /// </summary>
    private string[] CreateChain(int levels)
    {
        var paths = new string[levels + 1];
        var path = Path.Combine(_root, "devices");
        for (var i = 0; i <= levels; i++)
        {
            path = Path.Combine(path, "n" + i);
            paths[i] = path;
        }

        Directory.CreateDirectory(path);
        return paths;
    }

    private static string CreateSubdirectory(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteIds(string deviceNode, string vendorText, string productText)
    {
        File.WriteAllText(Path.Combine(deviceNode, "idVendor"), vendorText);
        File.WriteAllText(Path.Combine(deviceNode, "idProduct"), productText);
    }

    /// <summary>
    /// Creates the tty class entry for <paramref name="ttyName"/> with its <c>device</c> entry as a
    /// symlink to <paramref name="target"/>, the way the kernel lays it out.
    /// </summary>
    private void LinkTty(string ttyName, string target)
        => Directory.CreateSymbolicLink(Path.Combine(CreateSubdirectory(_root, ttyName), "device"), target);

    #endregion
}
