# Artwork loading

`ImageCache` shares encoded bytes in a bounded memory cache (64 MiB by default).
Remote images also use `Images/` on disk. File URIs are read directly, including
embedded covers already extracted by `FolderImportService` into `Artwork/`.
Invalidating a local image evicts its memory entry without deleting the source.

Known-length streams are read into one exact-sized array. Unknown-length streams
use a pooled transfer buffer and retain the resulting memory stream's backing
array. The cache accounts for the entire backing array, including unused capacity.
Decoding borrows the cached array through a read-only stream without copying it.

`ArtworkService` caches decoded images by absolute URI and decode width. Its LRU
budget defaults to 64 MiB and conservatively counts both estimated bitmap pixels
(four bytes per pixel) and the retained encoded buffer. The encoded buffer can be
shared across sizes. Active callers can keep an image alive after cache eviction;
their references are additional to the cache budget.

Both load methods return an independently disposable `LoadedArtwork` reference.
Consumers display `reference.Bitmap` and dispose the reference when replacing or
detaching the image. They must not dispose `reference.Bitmap` directly. The bitmap
is released when the cache, in-flight request, and all consumers release it.

Concurrent requests for the same key share one worker. Native decoding is limited
to two operations at a time. Cancelling one consumer does not cancel other
consumers. Once the last waiter leaves, queued work is cancelled. A synchronous
native decoder already in progress cannot be interrupted: its slot stays occupied
until it returns, and its abandoned bitmap is disposed. Service shutdown waits for
these workers before disposing the concurrency limiter.

Track lists reuse rows by playlist-item ID, queue-item ID, or track identity.
Metadata and row numbers update in place; artwork bindings only change when the
URI changes. Collection updates insert, remove, or move affected rows without a
reset. Filtering waits for a 200 ms typing pause and reuses the existing source
rows; clearing the filter is immediate. Language changes update displayed text
without constructing new rows. The queue drawer follows the same rules.

Run regression tests with:

```sh
dotnet test tests/LemonNeo.Tests/LemonNeo.Tests.csproj -p:SkipHostPlugins=true
```

The suite uses Avalonia Headless with the real Skia bitmap backend, an in-memory
HTTP handler, and isolated temporary folders. It covers shared decoding,
cancellation, eviction and disposal, corrupted disk data, byte-buffer reuse,
local-file storage, filtering, row identity, selection, and metadata updates.
