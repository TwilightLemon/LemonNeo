using System.Text;
using System.Buffers.Binary;

namespace LemonNeo.Hosting.Tests;

internal static class AudioTestFiles
{
    public static void WriteFlacMetadata(string path)
    {
        // A STREAMINFO-only fixture is enough for tag import; it is never played.
        var data = new byte[42];
        "fLaC"u8.CopyTo(data);
        data[4] = 0x80;
        data[7] = 34;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), 4096);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10), 4096);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(18), (44100UL << 44) | (1UL << 41) | (15UL << 36) | 44100UL);
        File.WriteAllBytes(path, data);
    }

    public static void WriteWave(string path, int bits = 16, int sampleRate = 44100)
    {
        var blockSize = 2 * bits / 8;
        var dataSize = sampleRate * blockSize; // One second, stereo PCM.
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8); writer.Write(36 + dataSize); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)2);
        writer.Write(sampleRate); writer.Write(sampleRate * blockSize);
        writer.Write((ushort)blockSize); writer.Write((ushort)bits);
        writer.Write("data"u8); writer.Write(dataSize); writer.Write(new byte[dataSize]);
    }
}
