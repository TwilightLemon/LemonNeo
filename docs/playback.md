# Playback requests and notifications

`PlaybackSessionService` owns selection, request cancellation, activation and the current
immutable snapshot. `CurrentTrack` is the logical queue track, even while loading or after
failure. The actual source identity belongs to `SourceDetails`. `CurrentQueueItemId`
distinguishes duplicate occurrences of the same track.

Each play request has a new `RequestId`, including retries of the same track. Snapshot
`Revision` increases on every committed change. Notifications are delivered in commit
order outside the session lock. Subscribers must return promptly.

## User playback

1. Accept the selection, revoke the previous request, and cancel its token.
2. Commit the selected queue item, logical metadata, reset timeline and `Loading` state.
3. Publish metadata, queue, state and timeline changes to the desktop and media adapters.
4. Resolve sources using the existing local-file, cache, quality and metadata expansion rules.
5. Prepare a request-owned decoder stream without holding the session or active-player lock.
6. Check ownership, activate the prepared stream, and commit `Playing` with source details.
7. Load playback lyrics, and persist discovered IDs after playback has started.

Only the current request can activate a stream or publish a result or error. A late result
from a cancelled request is disposed independently. Stop and queue clearing revoke the
current request too. User selections fail on the selected track rather than skipping it.

The desktop permits concurrent command submission; the session replaces old requests.
System media commands acknowledge submission without waiting for lookup or decoder startup.
Console commands can still await the request's actual completion.

## Notifications

| Event | Changes |
| --- | --- |
| `MetadataChanged` | Request identity, logical track, queue item identity, source and audio details |
| `TimelineChanged` | Position, duration, or a new request resetting the timeline |
| `StateChanged` | Loading, Playing, Paused, Stopped, Failed, or the request error |
| `QueueChanged` | Queue contents, current item, or playback mode |
| `VolumeChanged` | Application volume |

The 250 ms monitor samples playback and detects completion. It starts automatic advancement
as a separate request, so source discovery never blocks timeline monitoring or new commands.
The decoder's `PlayerState` remains separate from the host's `PlaybackState`.
Resume records also use `PlaybackState`, retaining the JSON `status` field and the existing
numeric values for Stopped (0), Playing (1) and Paused (2). Loading, Failed and unknown
values are normalized to Stopped when reading or saving a resume record.

## Automatic advancement

Natural completion traverses candidates according to the current mode. Every queue item is
tried at most once in that advance: sequential playback stops at the end; repeat-all wraps
once; shuffle produces a finite list of distinct items. Source unavailability is skipped.
Host configuration errors, such as a missing decoder, fail the advance. Cancellation is
neither an error nor a reason to skip. A user selection replaces the whole automatic advance.

## Lyrics

`PlaybackLyricsService` owns automatic lyrics independently of view lifetime. It clears and
cancels on a new request, starts once when that request reaches successful playback, and
guards results with the request identity. Pause, resume and seek do not restart lookup.
Desktop and Console read the same lyrics snapshot. Explicit inspection remains a separate
query through `LyricsService`.

`LyricsService` matches IDs already carried by the track against each `ILyricsProvider.AcceptedIdSchemes` collection.
It does not expand metadata or search by title/artist. Each provider receives the matching
`ExternalMusicId` (platform namespace, ID kind and value) through `GetLyricsAsync`.
Native track IDs have the explicit `track-id` kind;
other kinds must be declared in the track's external IDs rather than inferred from a platform name.

All known IDs read their shared platform cache first, even if the producing plugin is absent.
Uncached IDs are fetched concurrently from compatible plugins,
then one final result is selected: word timing before line timing before plain text, then the
number of auxiliary kinds (translation and romanization), then cached before fresh results.
Empty auxiliary data is ignored. Provider ID and remote ID give a stable final tie-break.
A cached result with word timing, translation and romanization can return without network requests.

The cache key is the normalized `ExternalMusicId` (namespace, kind and value), never the plugin
name. Multiple plugins fetching the same ID share one document; a query persists only its best
result for that ID. The optional cached provider ID records provenance and does not affect lookup.
`Lyrics.SourceId` also remains provenance and does not create cache aliases. The original
platform-ID cache format 1 is supported directly. The intermediate provider-scoped format 2
is treated as a miss.

The Lyricify main provider combines search, metadata matching and lyrics, accepting lyric ID
schemes from eight platforms.
QQ lyrics accepts `qqmusic:song-id`, not `song-mid`. Kugou resolves download credentials by
the known audio hash. Protocol requests, decryption and parsing remain in the plugin; matching
and selection are owned by the host.

## Build

Run `dotnet build LemonNeo.slnx`.
