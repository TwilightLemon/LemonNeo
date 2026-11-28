using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;
using Lyricify.Lyrics.Decrypter.Krc;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Providers.Web.Spotify;
using LemonLyrics = LemonNeo.Core.Lyrics.Lyrics;
using LemonLyricsLine = LemonNeo.Core.Lyrics.LyricsLine;

namespace LemonNeo.Plugin.Lyricify;

public sealed class LyricifyLyricsPlugin : ILemonPlugin, IMusicProvider, ILyricsProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public PluginDescriptor Descriptor { get; } = new(
        "LemonNeo.Plugin.Lyricify",
        "Lyricify Lyrics",
        "1.0.0");

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConfigureOptionalCredentials();
        context.RegisterProvider(this);
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public string Id => "lyricify";

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "Lyricify Lyrics",
        "Fetches lyrics by known platform IDs using Lyricify Lyrics Helper.");

    public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; } =
    [
        new(MusicIdNamespaces.QQMusic, "song-id"),
        new(MusicIdNamespaces.NeteaseMusic, "song-id"),
        new(MusicIdNamespaces.KugouMusic, "audio-hash"),
        new(MusicIdNamespaces.AppleMusic, "catalog-song-id"),
        new(MusicIdNamespaces.Spotify, "track-id"),
        new(MusicIdNamespaces.Musixmatch, "track-id"),
        new(MusicIdNamespaces.SodaMusic, "track-id"),
        new(MusicIdNamespaces.LrcLib, "lyrics-id")
    ];

    public Task<LemonLyrics?> GetLyricsAsync(ExternalMusicId musicId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scheme = AcceptedIdSchemes.FirstOrDefault(candidate => candidate.Accepts(musicId));
        if (scheme is null || string.IsNullOrWhiteSpace(musicId.Value))
            return Task.FromResult<LemonLyrics?>(null);
        var id = new ExternalMusicId(scheme.Namespace, musicId.Value, scheme.Kind);
        return scheme.Namespace switch
        {
            MusicIdNamespaces.QQMusic => FetchQqAsync(id, cancellationToken),
            MusicIdNamespaces.NeteaseMusic => FetchNeteaseAsync(id, cancellationToken),
            MusicIdNamespaces.KugouMusic => FetchKugouAsync(id, cancellationToken),
            MusicIdNamespaces.AppleMusic => FetchAppleAsync(id, cancellationToken),
            MusicIdNamespaces.Spotify => FetchSpotifyAsync(id, cancellationToken),
            MusicIdNamespaces.Musixmatch => FetchMusixmatchAsync(id, cancellationToken),
            MusicIdNamespaces.SodaMusic => FetchSodaAsync(id, cancellationToken),
            MusicIdNamespaces.LrcLib => FetchLrcLibAsync(id, cancellationToken),
            _ => Task.FromResult<LemonLyrics?>(null)
        };
    }

    private static async Task<LemonLyrics?> FetchQqAsync(ExternalMusicId id, CancellationToken token)
    {
        if (!long.TryParse(id.Value, out var number) || number <= 0) return null;
        var result = await new global::Lyricify.Lyrics.Providers.Web.QQMusic.Api()
            .GetLyricsAsync(id.Value).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(result?.Lyrics) ? null
            : Parse(result.Lyrics, LyricsRawTypes.Qrc, id, result.Trans, result.Romaji);
    }

    private static async Task<LemonLyrics?> FetchNeteaseAsync(ExternalMusicId id, CancellationToken token)
    {
        var result = await new global::Lyricify.Lyrics.Providers.Web.Netease.Api()
            .GetLyricNew(id.Value).WaitAsync(RequestTimeout, token);
        var raw = result?.Yrc?.Lyric;
        var type = LyricsRawTypes.Yrc;
        if (string.IsNullOrWhiteSpace(raw)) { raw = result?.Lrc?.Lyric; type = LyricsRawTypes.Lrc; }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var translation = FirstNonEmpty(type == LyricsRawTypes.Yrc ? result?.Ytlrc?.Lyric : null, result?.Tlyric?.Lyric);
        var romanization = FirstNonEmpty(type == LyricsRawTypes.Yrc ? result?.Yromalrc?.Lyric : null, result?.Romalrc?.Lyric);
        return Parse(raw, type, id, translation, romanization);
    }

    private static async Task<LemonLyrics?> FetchKugouAsync(ExternalMusicId id, CancellationToken token)
    {
        // Resolve download credentials using this recording's hash; no title/duration search.
        var response = await new global::Lyricify.Lyrics.Providers.Web.Kugou.Api()
            .GetSearchLyrics(hash: id.Value).WaitAsync(RequestTimeout, token);
        var candidate = response?.Candidates?.FirstOrDefault();
        if (candidate is null) return null;
        var raw = await Helper.GetLyricsAsync(candidate.Id, candidate.AccessKey).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(raw) ? null : Parse(raw, LyricsRawTypes.Krc, id);
    }

    private static async Task<LemonLyrics?> FetchAppleAsync(ExternalMusicId id, CancellationToken token)
    {
        var response = await new global::Lyricify.Lyrics.Providers.Web.AppleMusic.Api()
            .GetLyrics(id.Value).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(response?.Ttml) ? null : Parse(response.Ttml, LyricsRawTypes.Ttml, id);
    }

    private static async Task<LemonLyrics?> FetchSpotifyAsync(ExternalMusicId id, CancellationToken token)
    {
        var raw = await new global::Lyricify.Lyrics.Providers.Web.Spotify.Api()
            .GetLyrics(id.Value).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(raw) ? null : Parse(raw, LyricsRawTypes.Spotify, id);
    }

    private static async Task<LemonLyrics?> FetchMusixmatchAsync(ExternalMusicId id, CancellationToken token)
    {
        var raw = await new global::Lyricify.Lyrics.Providers.Web.Musixmatch.Api()
            .GetFullLyricsRaw(id.Value).WaitAsync(RequestTimeout, token);
        return string.IsNullOrWhiteSpace(raw) ? null : Parse(raw, LyricsRawTypes.Musixmatch, id);
    }

    private static async Task<LemonLyrics?> FetchSodaAsync(ExternalMusicId id, CancellationToken token)
    {
        var detail = await new global::Lyricify.Lyrics.Providers.Web.SodaMusic.Api()
            .GetDetail(id.Value).WaitAsync(RequestTimeout, token);
        var source = detail?.Lyric;
        if (string.IsNullOrWhiteSpace(source?.Content)) return null;
        var type = Enum.TryParse<LyricsRawTypes>(source.Type, true, out var declared)
            ? declared : TypeHelper.GetLyricsTypes(source.Content);
        var translation = source.Translations?.ChineseTranslation ?? source.LangTranslations?.Values
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value.Content))?.Content;
        return Parse(source.Content, type, id, translation) with
        {
            Language = string.IsNullOrWhiteSpace(source.Lang) ? null : source.Lang
        };
    }

    private static async Task<LemonLyrics?> FetchLrcLibAsync(ExternalMusicId id, CancellationToken token)
    {
        if (!int.TryParse(id.Value, out var number)) return null;
        var result = await new global::Lyricify.Lyrics.Providers.Web.LRCLIB.Api()
            .GetById(number).WaitAsync(RequestTimeout, token);
        if (!string.IsNullOrWhiteSpace(result?.SyncedLyrics)) return Parse(result.SyncedLyrics, LyricsRawTypes.Lrc, id);
        return string.IsNullOrWhiteSpace(result?.PlainLyrics) ? null : new LemonLyrics(result.PlainLyrics,
            Lines: result.PlainLyrics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => new LemonLyricsLine(line)).ToArray(), Source: id.Namespace, SourceId: id);
    }

    private static LemonLyrics Parse(string raw, LyricsRawTypes type, ExternalMusicId sourceId,
        string? translation = null, string? romanization = null) =>
        LyricsConverter.Parse(raw, type, sourceId.Namespace, sourceId, translation, romanization);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static void ConfigureOptionalCredentials()
    {
        var spotifySpDc = Environment.GetEnvironmentVariable("LEMONNEO_SPOTIFY_SP_DC");
        if (!string.IsNullOrWhiteSpace(spotifySpDc)) new Api().SetSpDc(spotifySpDc);

        var appleMediaUserToken = Environment.GetEnvironmentVariable("LEMONNEO_APPLE_MUSIC_USER_TOKEN");
        if (!string.IsNullOrWhiteSpace(appleMediaUserToken))
            new global::Lyricify.Lyrics.Providers.Web.AppleMusic.Api()
                .SetMediaUserToken(appleMediaUserToken);

        var musixmatchUserToken = Environment.GetEnvironmentVariable("LEMONNEO_MUSIXMATCH_USER_TOKEN");
        if (!string.IsNullOrWhiteSpace(musixmatchUserToken))
            new global::Lyricify.Lyrics.Providers.Web.Musixmatch.Api()
                .SetUserToken(musixmatchUserToken);
    }
}
