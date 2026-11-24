using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using LemonNeo.Application.Lyrics;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using TagLib;
using File = System.IO.File;

namespace LemonNeo.Application.Library;

public sealed record TrackExportResult(bool LyricsMissing, bool TagsUnsupported, bool ArtworkMissing);

public sealed partial class TrackExportService(PlaybackSourceDiscoveryService discovery, LyricsService lyrics,
    ImageCache images, HttpClient httpClient)
{
    public const string DefaultFileNameFormat = "T - A";

    public async Task<TrackExportResult> ExportAsync(Track track, string directory, AudioQuality quality,
        bool writeLyrics, bool writeTags, CancellationToken cancellationToken = default,
        string fileNameFormat = DefaultFileNameFormat, int? sequenceNumber = null)
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}");
        var audio = temporary + ".part";
        var lrc = temporary + ".lrc.part";
        try
        {
            var result = await discovery.FindAsync(track,
                new PlaybackRequest(quality) { PreferCachedResources = true }, cancellationToken);
            var candidate = result.Candidates.FirstOrDefault()
                ?? throw new IOException("No audio source is available.");
            var extension = await CopyAudioAsync(candidate.Source, audio, cancellationToken);
            var lyricsText = writeLyrics
                ? await GetLrcAsync(result.UpdatedTrack ?? track, candidate.SourceId, cancellationToken)
                : null;
            if (lyricsText is not null)
                await File.WriteAllTextAsync(lrc, lyricsText, Encoding.UTF8, cancellationToken);

            var tagsUnsupported = false;
            var artworkMissing = false;
            if (writeTags)
            {
                var artwork = track.ArtworkUri is { } uri ? await images.GetAsync(uri, cancellationToken) : null;
                tagsUnsupported = !WriteTags(audio, extension, track, artwork);
                artworkMissing = !tagsUnsupported && track.ArtworkUri is not null && artwork is null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Publish(audio, extension, lyricsText is not null ? lrc : null, directory,
                GetFileStem(track, fileNameFormat, sequenceNumber));
            return new TrackExportResult(writeLyrics && lyricsText is null, tagsUnsupported, artworkMissing);
        }
        finally
        {
            File.Delete(audio);
            File.Delete(lrc);
        }
    }

    private async Task<string> CopyAudioAsync(PlaybackSource source, string destination, CancellationToken token)
    {
        if (source is FilePlaybackSource local)
        {
            await using var input = new FileStream(local.Path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await CopyAudioAsync(input, destination, Path.GetExtension(local.Path), input.Length, token);
        }

        if (source is not HttpPlaybackSource { IsDirectAudio: true } http)
            throw new IOException("The source does not provide a downloadable audio file.");

        using var request = new HttpRequestMessage(HttpMethod.Get, http.Uri);
        foreach (var header in http.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is not null)
            throw new IOException("The server returned an incomplete audio file.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await CopyAudioAsync(stream, destination, Path.GetExtension(http.Uri.AbsolutePath),
            response.Content.Headers.ContentLength, timeout.Token);
    }

    private static async Task<string> CopyAudioAsync(Stream input, string destination, string extension,
        long? expectedLength, CancellationToken token)
    {
        var buffer = new byte[64];
        var length = await input.ReadAtLeastAsync(buffer, buffer.Length, false, token);
        extension = DetectExtension(buffer.AsSpan(0, length), extension.ToLowerInvariant());
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(buffer.AsMemory(0, length), token);
        await input.CopyToAsync(output, token);
        if (expectedLength is { } expected && output.Length != expected)
            throw new IOException("The downloaded audio length differs from the expected length.");
        return extension;
    }

    private static string DetectExtension(ReadOnlySpan<byte> bytes, string extension)
    {
        if (bytes.Length < 4) throw new IOException("The audio file is empty or incomplete.");
        if (bytes.StartsWith("fLaC"u8)) return ".flac";
        if (bytes.StartsWith("RIFF"u8) && bytes.Length >= 12 && bytes[8..].StartsWith("WAVE"u8)) return ".wav";
        if (bytes.StartsWith("FORM"u8) && bytes.Length >= 12 &&
            (bytes[8..].StartsWith("AIFF"u8) || bytes[8..].StartsWith("AIFC"u8))) return ".aiff";
        if (bytes.StartsWith("OggS"u8)) return bytes.IndexOf("OpusHead"u8) >= 0 ? ".opus" : ".ogg";
        if (bytes[4..].StartsWith("ftyp"u8)) return ".m4a";
        if (bytes.StartsWith(new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11 })) return ".wma";
        if (bytes[0] == 0xff && (bytes[1] & 0xf6) == 0xf0) return ".aac";
        if (bytes.StartsWith("ID3"u8) || (bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0))
            return extension is ".aac" or ".mp1" or ".mp2" ? extension : ".mp3";
        throw new IOException("The audio format could not be identified.");
    }

    private async Task<string?> GetLrcAsync(Track track, ExternalMusicId sourceId, CancellationToken token)
    {
        track = track with { ExternalIds = track.ExternalIds.Append(sourceId).DistinctBy(MusicCacheIdentity.GetKey).ToArray() };
        var result = await lyrics.GetLyricsAsync(track, token);
        if (result.Lyrics is not { } document) return null;
        var text = new StringBuilder();
        foreach (var row in LyricsDocument.Create(document))
        {
            var line = row.Main;
            var content = line.IsWordSynchronized
                ? string.Concat(line.Words!.Select(word => word.Text))
                : line.Text;
            if (line.EffectiveStartTimeMs is >= 0 and var time)
                text.Append(FormattableString.Invariant($"[{time / 60000:00}:{time / 1000 % 60:00}.{time / 10 % 100:00}]"));
            text.AppendLine(content.Replace('\r', ' ').Replace('\n', ' '));
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    private static bool WriteTags(string path, string extension, Track track, CachedImage? artwork)
    {
        TagLib.File file;
        try
        {
            file = TagLib.File.Create(path, "taglib/" + extension[1..], ReadStyle.None);
        }
        catch (UnsupportedFormatException)
        {
            return false;
        }

        using (file)
        {
            file.Tag.Title = track.Title;
            if (track.Artists.Count > 0) file.Tag.Performers = track.Artists.Select(artist => artist.Name).ToArray();
            if (track.Album is { } album) file.Tag.Album = album.Title;
            if (artwork is not null)
                file.Tag.Pictures = [new Picture(new ByteVector(artwork.Data.ToArray())) { Type = PictureType.FrontCover }];
            file.Save();
        }

        return true;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])[ITA](?![A-Za-z0-9])")]
    private static partial Regex FileNameTokens();

    [GeneratedRegex(@"(?<![A-Za-z0-9])I(?![A-Za-z0-9])[ \t]*")]
    private static partial Regex SequenceTokenWithSpacing();

    private static string GetFileStem(Track track, string format, int? sequenceNumber)
    {
        var artists = string.Join(", ", track.Artists.Select(artist => artist.Name));
        format = string.IsNullOrWhiteSpace(format) ? DefaultFileNameFormat : format.Trim();
        if (sequenceNumber is null) format = SequenceTokenWithSpacing().Replace(format, "").Trim();
        if (format == DefaultFileNameFormat && string.IsNullOrWhiteSpace(artists)) format = "T";
        var label = FileNameTokens().Replace(format, token => token.Value switch
        {
            "I" => sequenceNumber?.ToString(CultureInfo.InvariantCulture) ?? "",
            "T" => track.Title,
            "A" => artists,
            _ => token.Value
        });
        if (string.IsNullOrWhiteSpace(label)) label = track.Title;
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var stem = new string(label.Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c)
            .Take(100).ToArray()).Trim(' ', '.');
        if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        if (stem.Length == 0) return "Track";
        var firstPart = stem.Split('.')[0].TrimEnd();
        return firstPart.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
               (firstPart.Length == 4 && "123456789¹²³".Contains(firstPart[3]) &&
                (firstPart.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 firstPart.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            ? "_" + stem : stem;
    }

    private static void Publish(string audio, string extension, string? lyrics, string directory, string stem)
    {
        for (var number = 1; ; number++)
        {
            var name = Path.Combine(directory, number == 1 ? stem : $"{stem} ({number})");
            var audioPath = name + extension;
            var lyricsPath = name + ".lrc";
            if (File.Exists(lyricsPath) || Directory.Exists(lyricsPath) || Directory.Exists(audioPath)) continue;
            try { File.Move(audio, audioPath); }
            catch (IOException) when (File.Exists(audioPath)) { continue; }
            if (lyrics is null) return;
            try { File.Move(lyrics, lyricsPath); }
            catch
            {
                File.Move(audioPath, audio);
                throw;
            }

            return;
        }
    }
}
