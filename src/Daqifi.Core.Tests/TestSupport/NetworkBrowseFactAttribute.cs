using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// A <see cref="FactAttribute"/> that reports the test as <em>skipped</em> when this host has no
/// adapter the network finders would browse on, rather than letting it run and assert nothing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WiFiDeviceFinder"/> and <see cref="MDnsDeviceFinder"/> end a pass immediately, with an
/// empty result, when no adapter passes <see cref="WiFiDeviceFinder.ShouldIncludeInterface"/> with
/// a usable IPv4 address. A hosted Windows CI runner, whose only adapter is Hyper-V, and a
/// loopback-only container are both like that. A test about what happens <em>during</em> a browse
/// would pass there without ever seeing one.
/// </para>
/// <para>
/// The check reuses the finders' own adapter filter, so it cannot drift from what they actually
/// browse on. As with <see cref="PlatformFactAttribute"/>, the decision is made in the constructor
/// because xunit 2.9.3 only honours a skip set at discovery.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NetworkBrowseFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkBrowseFactAttribute"/> class.
    /// </summary>
    /// <param name="requireMulticast">
    /// Whether the adapter must also support multicast, as <see cref="MDnsDeviceFinder"/> requires.
    /// </param>
    public NetworkBrowseFactAttribute(bool requireMulticast = false)
    {
        RequireMulticast = requireMulticast;

        if (!HasBrowsableAdapter(requireMulticast))
        {
            Skip = requireMulticast
                ? "No multicast-capable adapter the finders browse on, so an mDNS pass ends before it starts."
                : "No adapter the finders browse on, so a broadcast pass ends before it starts.";
        }
    }

    /// <summary>Gets whether the adapter must also support multicast.</summary>
    public bool RequireMulticast { get; }

    private static bool HasBrowsableAdapter(bool requireMulticast)
    {
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return false;
        }

        foreach (var adapter in adapters)
        {
            if (requireMulticast && !adapter.SupportsMulticast)
            {
                continue;
            }

            if (!WiFiDeviceFinder.ShouldIncludeInterface(
                    adapter.Name,
                    adapter.Description,
                    adapter.OperationalStatus,
                    adapter.NetworkInterfaceType,
                    adapter.Supports(NetworkInterfaceComponent.IPv4)))
            {
                continue;
            }

            // The same address test both finders apply: IPv4, with a real subnet mask.
            if (adapter.GetIPProperties().UnicastAddresses.Any(address =>
                    address.Address.AddressFamily == AddressFamily.InterNetwork
                    && address.IPv4Mask is { } mask
                    && !mask.Equals(IPAddress.Any)))
            {
                return true;
            }
        }

        return false;
    }
}
