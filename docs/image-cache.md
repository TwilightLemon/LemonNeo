# Image caching and desktop artwork

`LemonNeo.Application.ImageCache` reads images in three tiers:

1. Memory: a dedicated `Microsoft.Extensions.Caching.Memory.MemoryCache` with a
   64 MiB image-byte budget by default (`SizeLimit`, with each entry sized to its
   byte length). Admission and eviction use the framework's cache policy.
2. Disk: `<IAppPathProvider.CacheDirectory>/Images/<sha256-of-uri>.image`.
3. Source: the original HTTP/HTTPS URL or `file:` URI.

The desktop host registers one cache and one `ArtworkService` for the whole
application. Its disk directory derives from the same resolved application cache
root used by music and lyrics. `LEMONNEO_CACHE_HOME` overrides that root through
the existing application path configuration; there is no separate image path
setting. **Tools → Paths** also displays the resolved image cache directory.

The image service owns and disposes its memory cache. It does not impose a size
limit on the application's shared `IMemoryCache`. Images that are not admitted to
memory remain available on disk; setting the image-byte budget to zero disables
the memory tier. Request coalescing and cancellation are managed separately from
`MemoryCache` so simultaneous misses still share one source request.

Keys include the full absolute URI, including its query string. Tracks with the
same cover share bytes regardless of their provider or track identity. The cache
stores original image bytes, so thumbnails and the playing cover can decode at
different sizes. Disk entries remain available after restart and work offline.

Concurrent requests for a URI share one operation. Cancelling one caller cancels
its wait, allowing other callers to finish. At most four cache misses run at once;
each has a 15-second timeout after acquiring a slot. Images are limited to 16 MiB,
including responses without a declared content length. Disk writes use unique
temporary files and an atomic rename. Disk access errors allow source loading and
memory caching to continue. Cache disposal cancels and joins outstanding requests.

`ArtworkService` decodes images off the UI thread. Every call returns a separately
owned bitmap: releasing a row's image cannot invalidate a playing cover or another
row. Invalid images are evicted; a cached image that cannot be decoded gets one
fresh source attempt. Download and decode failures return no cover.

The shared desktop `Artwork` control accepts `SourceUri` and `Loader` for rows,
while its existing `Source` property supports playback artwork. It requests row
thumbnails only while attached to the visual tree and releases their bitmaps on
detach or rebinding. Cancellation and request identity checks prevent a delayed
cover from replacing a newer row's image. Playlist, library, search, queue, and
metadata match rows use rounded rectangular covers. Missing, unsupported, failed,
or loading artwork displays the control's themed music placeholder.

## Verification

```powershell
dotnet test tests/LemonNeo.Desktop.Tests/LemonNeo.Desktop.Tests.csproj -p:SkipDesktopPlugins=true
```

`ImageCacheTests` verifies tier order, restart/offline reuse, the memory budget,
shared requests and independent cancellation, shutdown, URL separation, source
failures, write failures, local files, corrupt entries, and bitmap ownership.
`TrackArtworkTests` renders the playlist, library, search, and queue views to check
actual covers, rounded clipping, placeholders, and detach/rebind behavior. Existing
playback artwork tests also use the shared cache.

To save the four rendered list previews:

```powershell
$env:LEMONNEO_ARTWORK_PREVIEW_DIR = Join-Path (Get-Location) 'artifacts/artwork-preview'
dotnet test tests/LemonNeo.Desktop.Tests/LemonNeo.Desktop.Tests.csproj -p:SkipDesktopPlugins=true --filter FullyQualifiedName~TrackArtworkTests
```
