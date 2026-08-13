using System;
using System.IO;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// A parse source that can be read from the beginning more than once.
/// <para>
/// This is what lets <see cref="SdCardLogSession.Samples"/> stay lazy. The parsers read a
/// short prefix to work out the device configuration, then hand the session an iterator that
/// re-reads the source from the start each time it is enumerated — so no more than one read
/// buffer of the file is ever resident.
/// </para>
/// </summary>
internal sealed class SdCardParseSource
{
    private readonly Stream? _stream;
    private readonly long _origin;
    private readonly string? _filePath;
    private readonly int _bufferSize;

    private SdCardParseSource(Stream stream)
    {
        _stream = stream;
        _origin = stream.Position;
        TotalBytes = stream.Length;
    }

    private SdCardParseSource(string filePath, int bufferSize)
    {
        _filePath = filePath;
        _bufferSize = bufferSize;
        TotalBytes = -1;
    }

    /// <summary>
    /// Gets the total size of the source in bytes, or <c>-1</c> when it is not yet known
    /// (a file source learns its length when it is first opened).
    /// </summary>
    public long TotalBytes { get; private set; }

    /// <summary>
    /// Wraps a caller-supplied stream, or returns <c>null</c> when the stream cannot be
    /// rewound and therefore can only be read once.
    /// </summary>
    /// <param name="stream">The stream to wrap.</param>
    /// <returns>A rewindable source, or <c>null</c> for a forward-only stream.</returns>
    public static SdCardParseSource? TryCreate(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.CanSeek ? new SdCardParseSource(stream) : null;
    }

    /// <summary>
    /// Creates a source that opens its own <see cref="FileStream"/> on every read.
    /// </summary>
    /// <param name="filePath">The path to read.</param>
    /// <param name="bufferSize">The stream buffer size in bytes.</param>
    /// <returns>A rewindable source over the file.</returns>
    public static SdCardParseSource ForFile(string filePath, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return new SdCardParseSource(filePath, bufferSize);
    }

    /// <summary>
    /// Opens the source positioned at its start. Dispose the returned lease when the read
    /// is finished; it closes the stream only when the source owns it.
    /// </summary>
    /// <returns>A lease over a readable stream positioned at the start of the source.</returns>
    public Lease Open()
    {
        if (_filePath is not null)
        {
            var fileStream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _bufferSize,
                useAsync: true);

            TotalBytes = fileStream.CanSeek ? fileStream.Length : -1;
            return new Lease(fileStream, ownsStream: true);
        }

        _stream!.Position = _origin;
        return new Lease(_stream, ownsStream: false);
    }

    /// <summary>
    /// A borrowed read over an <see cref="SdCardParseSource"/>.
    /// </summary>
    internal readonly struct Lease : IAsyncDisposable
    {
        private readonly bool _ownsStream;

        internal Lease(Stream stream, bool ownsStream)
        {
            Stream = stream;
            _ownsStream = ownsStream;
        }

        /// <summary>
        /// Gets the readable stream, positioned at the start of the source.
        /// </summary>
        public Stream Stream { get; }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _ownsStream ? Stream.DisposeAsync() : default;
    }
}
