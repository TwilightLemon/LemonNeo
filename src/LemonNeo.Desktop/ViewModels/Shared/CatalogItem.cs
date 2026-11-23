using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Shared;

public sealed record CatalogItem(SearchKind Kind, MusicItemId Id, string Name, Uri? ArtworkUri = null,
    string? Subtitle = null, ArtworkService? ArtworkLoader = null, bool ResolveFromTrack = false);
