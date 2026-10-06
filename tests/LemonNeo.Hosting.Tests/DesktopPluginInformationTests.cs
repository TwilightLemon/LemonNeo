using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shell;
using LemonNeo.Desktop.Views.Tools;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class DesktopPluginInformationTests
{
    [AvaloniaFact]
    public async Task PluginOverviewSeparatesCatalogNamespacesFromPlaybackAndUpdatesItsLocalizedCards()
    {
        using var paths = new TemporaryPaths();
        await using var host = new DesktopHost(paths,
            new(Path.Combine(AppContext.BaseDirectory, "fixtures"), paths.MusicLibraryDirectory),
            [new TestPlayer()], [new TestMusic(), new CatalogProvider(), new AliasedPlaybackProvider()]);
        var scope = host.Settings.ForPackage(PackageNames.Desktop);
        DesktopPreferences.Register(scope);
        using var text = new LocalizationService(scope);
        text.Apply(LanguagePreference.English);
        using var appearance = new AppearanceService(Avalonia.Application.Current!, scope);
        await using var main = new MainViewModel(host, text, appearance, host.Get<ArtworkService>());
        await main.InitializeAsync();
        Assert.True(main.SharedContext.IsInitialized);
        var tools = main.Tools;
        Assert.Equal(["applemusic", "file", "netease", "qqmusic", "spotify"], tools.SupportedNamespaces, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["file", "netease"], tools.PlayableNamespaces);
        Assert.All(host.Get<IProviderRegistry>().Providers, provider =>
        {
            Assert.DoesNotContain(provider.Id, tools.SupportedNamespaces);
            Assert.DoesNotContain(provider.Id, tools.PlayableNamespaces);
        });
        var cards = tools.ProviderGroups.SelectMany(group => group.Items).ToArray();
        Assert.Equal(5, cards.Length);
        Assert.Equal(cards.Length, cards.Select(card => card.Id).Distinct().Count());
        Assert.Empty(Assert.Single(cards, card => card.Id == "metadata-broker").PlayableNamespaces);
        var undeclared = Assert.Single(cards, card => card.Id == "acceptance-music");
        Assert.Empty(undeclared.SupportedNamespaces);
        Assert.Empty(undeclared.PlayableNamespaces);
        var playback = Assert.Single(cards, card => card.Id == "alias-playback");
        Assert.Equal(["file", "netease"], playback.SupportedNamespaces);
        Assert.Equal(["file", "netease"], playback.PlayableNamespaces);
        var plugin = Assert.Single(host.Plugins.Loaded);
        Assert.Contains(cards, card => card.Id == plugin.Manifest.Id && card.HasDetails);

        var view = new ToolsView { DataContext = tools };
        var window = new Window { Content = view, Width = 720, Height = 800 };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(tools.SupportedNamespaces, view.FindControl<ItemsControl>("SupportedNamespacesList")!.Items.Cast<string>());
            Assert.Equal(tools.PlayableNamespaces, view.FindControl<ItemsControl>("PlayableNamespacesList")!.Items.Cast<string>());
            var visibleText = view.GetVisualDescendants().OfType<TextBlock>().Select(control => control.Text).ToArray();
            Assert.Contains("Supported namespaces", visibleText);
            Assert.Contains("Metadata & lyrics providers", visibleText);
            Assert.DoesNotContain(text["Storage"], visibleText);
            Assert.DoesNotContain(main.SharedContext.LibrarySummary, visibleText);

            text.Apply(LanguagePreference.Chinese);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(tools.ProviderGroups, group => group.Name == "元数据与歌词提供方");
            Assert.Contains("支持的命名空间", view.GetVisualDescendants().OfType<TextBlock>().Select(control => control.Text));
            Assert.Equal(["file", "netease"], tools.PlayableNamespaces);
        }
        finally { window.Close(); }
    }

    private sealed class CatalogProvider : IMusicProvider, ISearchNamespaceProvider, IMetadataProvider
    {
        public string Id => "metadata-broker";
        public ProviderDescriptor Descriptor { get; } = new("Catalog", "Catalog sources include services without playback support.");
        public IReadOnlyCollection<string> SearchNamespaces => ["applemusic", " QQMUSIC "];
        public IReadOnlyCollection<string> MetadataNamespaces => ["qqmusic", "netease", "spotify", " ", "QQMUSIC"];
        public Task<MetadataLookupResult> LookupMetadataAsync(MetadataLookupRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The information page must not perform a metadata lookup.");
    }

    private sealed class AliasedPlaybackProvider : IMusicProvider, IPlaybackProvider, ITrackIdResolverProvider
    {
        public string Id => "alias-playback";
        public ProviderDescriptor Descriptor { get; } = new("Playback source", "Consumes file and Netease identifiers.");
        public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes => [new("file", "path"), new("netease", "song-id"), new("netease", "other-id")];
        public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The information page must not resolve audio.");
        public Task<Track?> ResolveTrackIdAsync(ExternalMusicId identifier, TrackMetadata metadata, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The information page must not resolve external identifiers.");
    }
}
