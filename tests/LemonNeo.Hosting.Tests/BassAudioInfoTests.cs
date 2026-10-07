using System.Runtime.InteropServices;
using LemonNeo.Core.Playback;
using LemonNeo.Plugin.Bass;
using Un4seen.Bass;
using Xunit;
using BassApi = Un4seen.Bass.Bass;

namespace LemonNeo.Hosting.Tests;

public sealed class BassAudioInfoTests
{
    [Theory]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_FLAC, 16, 44100, 900, AudioQuality.Lossless)]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_FLAC, 24, 44100, 900, AudioQuality.HiRes)]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_FLAC, 16, 96000, 900, AudioQuality.HiRes)]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_FLAC, 16, 44100, 100, AudioQuality.Lossless)]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_WAV_PCM, 16, 48000, 1536, AudioQuality.Lossless)]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_WAV_FLOAT, 0x10020, 44100, 2822, AudioQuality.HiRes)]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_MP3, 0, 44100, 128, AudioQuality.Low)]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_MP3, 0, 44100, 192, AudioQuality.Medium)]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_MP3, 0, 44100, 320, AudioQuality.High)]
    [InlineData(BASSChannelType.BASS_CTYPE_STREAM_AAC, 0, 96000, 512, AudioQuality.High)]
    public void HostClassifiesOriginalFormatInsteadOfFloatDecoderOutput(
        BASSChannelType type, int bits, int rate, float bitrate, AudioQuality quality)
    {
        var info = BassAudioInfo.FromChannel(new BASS_CHANNELINFO
        {
            ctype = type, origres = bits, freq = rate, chans = 2, flags = BASSFlag.BASS_SAMPLE_FLOAT
        }, bitrate);

        Assert.Equal(quality, AudioQualities.Classify(info));
        Assert.Equal(bits == 0 ? (int?)null : bits & 0xffff, info.BitsPerSample);
        Assert.Equal((int)bitrate, info.BitrateKbps);
        Assert.Equal(2, info.Channels);
    }

    [Fact]
    public void UnknownFormatAndInvalidMeasurementsAreNotInvented()
    {
        Assert.Null(AudioQualities.Classify(BassAudioInfo.FromChannel(null, 2000)));
        Assert.Null(BassAudioInfo.FromChannel(null, float.NaN).BitrateKbps);
        Assert.Null(BassAudioInfo.FromChannel(null, float.PositiveInfinity).BitrateKbps);
        Assert.Null(BassAudioInfo.FromChannel(null, 0).BitrateKbps);
        Assert.Null(BassAudioInfo.FromChannel(new BASS_CHANNELINFO(), null).BitsPerSample);
        Assert.Null(AudioQualities.Resolve(new AudioStreamInfo(IsLossless: false), AudioQuality.HiRes));
        Assert.Equal(AudioQuality.High, AudioQualities.Resolve(null, AudioQuality.High));
    }

    [Theory]
    [InlineData(16, 44100, AudioQuality.Lossless)]
    [InlineData(24, 96000, AudioQuality.HiRes)]
    public void NativeBassReportsWaveBitrateAndOriginalBitDepth(int bits, int rate, AudioQuality quality)
    {
        if (!OperatingSystem.IsWindows()) return; // This repository bundles native BASS for Windows only.
        NativeBass.EnsureLoaded();
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "audio.wav");
        AudioTestFiles.WriteWave(path, bits, rate);
        Assert.True(BassApi.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero)); // No audio output.
        try
        {
            var stream = BassApi.BASS_StreamCreateFile(path, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);
            Assert.NotEqual(0, stream);
            try
            {
                float bitrate = 0;
                Assert.True(BassApi.BASS_ChannelGetAttribute(stream, BASSAttribute.BASS_ATTRIB_BITRATE, ref bitrate));
                var info = BassAudioInfo.FromChannel(BassApi.BASS_ChannelGetInfo(stream), bitrate);
                Assert.Equal(bits, info.BitsPerSample);
                Assert.Equal(rate, info.SampleRateHz);
                Assert.InRange(info.BitrateKbps!.Value, (int)(rate * bits * 2 / 1000d) - 1, (int)(rate * bits * 2 / 1000d) + 1);
                Assert.Equal(quality, AudioQualities.Classify(info));
            }
            finally { BassApi.BASS_StreamFree(stream); }
        }
        finally { BassApi.BASS_Free(); }
    }

    private static class NativeBass
    {
        static NativeBass() => NativeLibrary.SetDllImportResolver(typeof(BassApi).Assembly, (name, _, _) =>
            name.Equals("bass", StringComparison.OrdinalIgnoreCase) || name.Equals("bass.dll", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), "native", "bass.dll"))
                : IntPtr.Zero);
        public static void EnsureLoaded() { }
    }
}
