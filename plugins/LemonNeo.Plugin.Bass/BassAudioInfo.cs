using LemonNeo.Core.Playback;
using Un4seen.Bass;

namespace LemonNeo.Plugin.Bass;

internal static class BassAudioInfo
{
    internal static AudioStreamInfo FromChannel(BASS_CHANNELINFO? channel, float? bitrate)
    {
        var (codec, lossless) = channel?.ctype switch
        {
            BASSChannelType.BASS_CTYPE_STREAM_FLAC or BASSChannelType.BASS_CTYPE_STREAM_FLAC_OGG => ("FLAC", (bool?)true),
            BASSChannelType.BASS_CTYPE_STREAM_ALAC => ("ALAC", true),
            BASSChannelType.BASS_CTYPE_STREAM_APE => ("APE", true),
            BASSChannelType.BASS_CTYPE_STREAM_WAV_PCM => ("PCM", true),
            BASSChannelType.BASS_CTYPE_STREAM_WAV_FLOAT => ("IEEE float PCM", true),
            BASSChannelType.BASS_CTYPE_STREAM_MP1 => ("MP1", false),
            BASSChannelType.BASS_CTYPE_STREAM_MP2 => ("MP2", false),
            BASSChannelType.BASS_CTYPE_STREAM_MP3 or BASSChannelType.BASS_CTYPE_STREAM_WMA_MP3 => ("MP3", false),
            BASSChannelType.BASS_CTYPE_STREAM_AAC => ("AAC", false),
            BASSChannelType.BASS_CTYPE_STREAM_OGG => ("Vorbis", false),
            BASSChannelType.BASS_CTYPE_STREAM_OPUS => ("Opus", false),
            // Containers/platform codecs can contain lossy or lossless audio.
            // Do not claim losslessness based on an extension or a high bitrate.
            _ => ((string?)null, null)
        };
        var bits = channel?.origres & 0xffff; // BASS_ORIGRES_FLOAT lives outside the LOWORD.
        return new(
            bitrate is > 0 and < int.MaxValue && float.IsFinite(bitrate.Value) ? Math.Max(1, (int)Math.Round(bitrate.Value)) : null,
            channel?.freq is > 0 ? channel.freq : null,
            bits is > 0 ? bits : null,
            channel?.chans is > 0 ? channel.chans : null,
            lossless, codec);
    }
}
