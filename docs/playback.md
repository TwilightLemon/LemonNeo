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

The `ILyricsProvider.GetLyricsAsync(Track)` contract and Lyricify's internal matching remain
in place. Moving to provider-scoped music IDs and host-owned lyrics selection is deferred.

## Validation

Run `dotnet test tests/LemonNeo.Tests/LemonNeo.Tests.csproj -p:SkipHostPlugins=true`.
Tests use controlled source/decoder completions to check request replacement, late results,
metadata timing, notification separation, failed selection identity and finite automatic
skipping. The native BASS test opens a deliberately stalled loopback HTTP stream while
preparing and activating another stream, using BASS's no-sound device.
