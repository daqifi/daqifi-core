using System.Reflection;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Build;

/// <summary>
/// Guards against a public interface that a consumer can implement but never hand to Core, and
/// never receive from it: a test seam whose public entry points were all made internal while the
/// interface itself was left behind. Such an interface is dead weight in the public API - nothing
/// outside Core can use it, yet changing it after a release is a breaking change.
/// </summary>
/// <remarks>
/// <c>IUsbLocationProvider</c> was exactly this after the seam-carrying
/// <c>FirmwareUpdateService</c> constructor became internal (#1028): the interface stayed public
/// while its implementations, its factory and every constructor accepting it were internal.
/// </remarks>
public class PublicInterfaceReachabilityTests
{
    private const BindingFlags VisibleMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    [Fact]
    public void EveryPublicInterface_CanBeGivenToOrObtainedFromThePublicApi()
    {
        var assembly = typeof(DaqifiDevice).Assembly;
        var exported = assembly.GetExportedTypes();

        var unreachable = exported
            .Where(type => type.IsInterface)
            .Where(candidate => !exported.Any(type => type != candidate && Uses(type, candidate)))
            .Where(candidate => !IsACapabilityOfAPublicType(candidate, assembly.GetTypes(), exported))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(unreachable.Count == 0,
            "These public interfaces appear in no other public type's signature and no object "
            + "the public API hands out implements them, so nothing outside Core can use them. "
            + "Make them internal or expose the member that takes them:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unreachable));
    }

    [Fact]
    public void TheUsbLocationProviderSeam_IsInternal_ButTheLocationKeyStaysPublic()
    {
        // The seam is Core's own; what discovery callers need is the key it produces.
        Assert.False(typeof(IUsbLocationProvider).IsVisible);

        var locationKey = typeof(IDeviceInfo).GetProperty(nameof(IDeviceInfo.LocationKey));
        Assert.NotNull(locationKey);
        Assert.True(typeof(IDeviceInfo).IsVisible);
        Assert.True(locationKey.GetMethod!.IsPublic);
    }

    [Fact]
    public void TheReachabilityCheck_SeesInterfacesThatAreInUse()
    {
        // Anti-vacuity guard: if Uses ever stopped matching, the first test would fail loudly, but
        // if GetExportedTypes ever returned no interfaces it would pass while checking nothing.
        var exported = typeof(DaqifiDevice).Assembly.GetExportedTypes();

        Assert.Contains(typeof(IDeviceInfo), exported);
        Assert.Contains(exported, type => type != typeof(IDeviceInfo) && Uses(type, typeof(IDeviceInfo)));

        // And the cast route must not wave everything through: Core's own implementations of the
        // location-provider seam are internal classes that are nothing else.
        var assembly = typeof(DaqifiDevice).Assembly;
        Assert.True(IsACapabilityOfAPublicType(typeof(INetworkAddressedDevice), assembly.GetTypes(), exported));
        Assert.False(IsACapabilityOfAPublicType(typeof(IUsbLocationProvider), assembly.GetTypes(), exported));
    }

    /// <summary>
    /// Whether a caller can reach <paramref name="candidate"/> by casting an object it already
    /// holds as some other public type: the capability-interface pattern, where an internal class
    /// such as <c>NetworkStreamingDevice</c> is handed out as a <c>DaqifiStreamingDevice</c> and
    /// the caller asks <c>device is INetworkAddressedDevice</c>. An internal class that is nothing
    /// but an implementation of the candidate does not count - no public member can return it.
    /// </summary>
    private static bool IsACapabilityOfAPublicType(Type candidate, Type[] allTypes, Type[] exported) =>
        allTypes.Any(type => !type.IsInterface
            && candidate.IsAssignableFrom(type)
            && exported.Any(other => other != candidate
                && !other.IsAssignableFrom(candidate)
                && other.IsAssignableFrom(type)));

    /// <summary>
    /// Whether <paramref name="type"/> implements or extends <paramref name="candidate"/>, or
    /// names it in the signature of a member visible outside the assembly.
    /// </summary>
    private static bool Uses(Type type, Type candidate)
    {
        if (type.GetInterfaces().Any(i => Mentions(i, candidate)))
        {
            return true;
        }

        if (type.IsGenericTypeDefinition
            && type.GetGenericArguments().Any(a => a.GetGenericParameterConstraints().Any(c => Mentions(c, candidate))))
        {
            return true;
        }

        foreach (var member in type.GetMembers(VisibleMembers))
        {
            switch (member)
            {
                case MethodBase method when IsVisibleOutside(method):
                    if (method.GetParameters().Any(p => Mentions(p.ParameterType, candidate))
                        || (method is MethodInfo info && Mentions(info.ReturnType, candidate))
                        || (method.IsGenericMethodDefinition && method.GetGenericArguments()
                            .Any(a => a.GetGenericParameterConstraints().Any(c => Mentions(c, candidate)))))
                    {
                        return true;
                    }

                    break;
                case FieldInfo field when (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)
                                          && Mentions(field.FieldType, candidate):
                    return true;
            }
        }

        return false;
    }

    private static bool IsVisibleOutside(MethodBase method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool Mentions(Type signature, Type candidate)
    {
        if (signature.HasElementType)
        {
            return Mentions(signature.GetElementType()!, candidate);
        }

        if (signature.IsGenericType)
        {
            if (signature.GetGenericTypeDefinition() == candidate)
            {
                return true;
            }

            return signature.GetGenericArguments().Any(a => Mentions(a, candidate));
        }

        return signature == candidate;
    }
}
