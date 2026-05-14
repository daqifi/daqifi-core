namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// No-op descriptor provider that returns null for every port. Used as the
/// fallback on platforms where USB descriptor enumeration isn't implemented,
/// or when callers want to preserve the legacy "probe every port" behavior.
/// </summary>
internal sealed class NullUsbPortDescriptorProvider : IUsbPortDescriptorProvider
{
    public static readonly NullUsbPortDescriptorProvider Instance = new();

    private NullUsbPortDescriptorProvider() { }

    public UsbPortDescriptor? GetDescriptor(string portName) => null;
}
