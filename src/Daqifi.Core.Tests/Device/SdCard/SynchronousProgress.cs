using System;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// A synchronous <see cref="IProgress{T}"/> implementation for tests.
/// Unlike <see cref="Progress{T}"/>, this invokes the callback inline on the
/// calling thread, avoiding race conditions in assertions.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
