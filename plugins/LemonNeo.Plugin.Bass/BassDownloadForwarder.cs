using System.Runtime.InteropServices;
using LemonNeo.Plugin.Abstractions;
using Un4seen.Bass;
using BassApi = Un4seen.Bass.Bass;

namespace LemonNeo.Plugin.Bass;

/// <summary>
/// Native callback adapter only: it forwards the same bytes BASS is playing to
/// the host. It has no cache paths, files, identities, or persistence policy.
/// </summary>
internal sealed class BassDownloadForwarder : IDisposable
{
    private readonly object _gate = new();
    private readonly IPlaybackDownloadSink _sink;
    private byte[] _buffer = [];
    private bool _disposed;

    public BassDownloadForwarder(IPlaybackDownloadSink sink)
    {
        _sink = sink;
        DownloadCallback = Receive;
    }

    public DOWNLOADPROC DownloadCallback { get; }

    public void SetStream(int stream)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var length = BassApi.BASS_StreamGetFilePosition(stream, BASSStreamFilePosition.BASS_FILEPOS_SIZE);
            // Bytes and EOF may have arrived before StreamCreateURL returned.
            _sink.SetExpectedLength(length > 0 ? length : null);
        }
    }

    private void Receive(nint buffer, int length, nint user)
    {
        // Never unwind through the unmanaged boundary. This lock is independent
        // of the player's lock, since StreamFree waits for callbacks to finish.
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (buffer == nint.Zero) _sink.Complete();
                else if (length > 0)
                {
                    if (_buffer.Length < length) _buffer = new byte[length];
                    Marshal.Copy(buffer, _buffer, 0, length);
                    using var chunk = new MemoryStream(_buffer, 0, length, writable: false);
                    _sink.Write(chunk);
                }
                // Non-null status/metadata callbacks with length zero are not audio.
            }
        }
        catch (Exception)
        {
            try { _sink.Abort(); }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _sink.Abort();
        }
    }
}
