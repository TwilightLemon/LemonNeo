using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using LemonNeo.Core.Playback;
using LemonNeo.Plugin.Bass;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using BassApi = Un4seen.Bass.Bass;
using static LemonNeo.Tests.PlaybackFixture;

namespace LemonNeo.Tests;

[CollectionDefinition("Native BASS", DisableParallelization = true)]
public sealed class BassCollection;

[Collection("Native BASS")]
public sealed class BassPlaybackTests
{
    static BassPlaybackTests()
    {
        NativeLibrary.SetDllImportResolver(typeof(BassApi).Assembly, (name, _, _) =>
        {
            if (!name.Contains("bass", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var file = os == "win" ? "bass.dll" : os == "osx" ? "libbass.dylib" : "libbass.so";
            return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", $"{os}-{arch}", "native", file));
        });
    }

    [Fact]
    public async Task SlowNativeNetworkOpenDoesNotBlockANewStreamAndCannotStopItWhenCancelled()
    {
        var wave = WaveFile();
        var file = Path.Combine(Path.GetTempPath(), $"LemonNeo-Bass-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(file, wave);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var entered = Signal<bool>();
        var release = Signal<bool>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            entered.TrySetResult(true);
            await release.Task.WaitAsync(timeout.Token);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wave.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, timeout.Token);
            await stream.WriteAsync(wave, timeout.Token);
        }, timeout.Token);
        try
        {
            // Device 0 decodes without requiring an audio output device on the build machine.
            await using var player = new BassPlayer(NullLogger<BassPlayer>.Instance, null, 0);
            using var cancelled = new CancellationTokenSource();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var slow = player.PrepareAsync(Track("slow"), new HttpPlaybackSource { Uri = new Uri($"http://127.0.0.1:{port}/slow.wav") },
                cancellationToken: cancelled.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var next = await player.PrepareAsync(Track("next"), new FilePlaybackSource(file))
                    .WaitAsync(TimeSpan.FromSeconds(2));
                next.Activate(0.25);
                Assert.Equal("next", player.CurrentTrack?.Title);
                cancelled.Cancel();
                release.TrySetResult(true);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);
                Assert.Equal("next", player.CurrentTrack?.Title);
                Assert.Equal(0.25, await player.GetVolumeAsync());
            }
            finally
            {
                release.TrySetResult(true);
                try { using var abandoned = await slow; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            release.TrySetResult(true);
            listener.Stop();
            await server;
            File.Delete(file);
        }
    }

    private static byte[] WaveFile()
    {
        const int dataLength = 44100 * 2;
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(dataLength + 36);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return data.ToArray();
    }
}
