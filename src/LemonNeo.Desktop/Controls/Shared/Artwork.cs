using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.Controls.Shared;

public sealed class Artwork : TemplatedControl
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<Artwork, IImage?>(nameof(Source));

    public static readonly StyledProperty<Uri?> SourceUriProperty =
        AvaloniaProperty.Register<Artwork, Uri?>(nameof(SourceUri));

    public static readonly StyledProperty<ArtworkService?> LoaderProperty =
        AvaloniaProperty.Register<Artwork, ArtworkService?>(nameof(Loader));

    private bool _attached;
    private LoadedArtwork? _loadedArtwork;
    private CancellationTokenSource? _request;

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Uri? SourceUri
    {
        get => GetValue(SourceUriProperty);
        set => SetValue(SourceUriProperty, value);
    }

    public ArtworkService? Loader
    {
        get => GetValue(LoaderProperty);
        set => SetValue(LoaderProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceUriProperty || change.Property == LoaderProperty) RefreshArtwork();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        RefreshArtwork();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        RefreshArtwork();
        base.OnDetachedFromVisualTree(e);
    }

    private void RefreshArtwork()
    {
        _request?.Cancel();
        _request = null;
        if (_loadedArtwork is { } previous)
        {
            _loadedArtwork = null;
            if (ReferenceEquals(Source, previous.Bitmap)) Source = null;
            previous.Dispose();
        }

        if (!_attached || SourceUri is not { } uri || Loader is not { } loader) return;
        var request = _request = new CancellationTokenSource();
        _ = LoadArtworkAsync(loader, uri, request);
    }

    private async Task LoadArtworkAsync(ArtworkService loader, Uri uri, CancellationTokenSource request)
    {
        LoadedArtwork? artwork = null;
        try
        {
            artwork = await loader.LoadAsync(uri, request.Token, 128);
            // Virtualized rows can be detached or rebound while their shared request is running.
            if (!_attached || request.IsCancellationRequested || !ReferenceEquals(_request, request)) return;
            _loadedArtwork = artwork;
            Source = artwork?.Bitmap;
            artwork = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            artwork?.Dispose();
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
        }
    }
}
