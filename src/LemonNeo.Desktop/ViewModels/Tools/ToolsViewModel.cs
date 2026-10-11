using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Application.Playback;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Tools;

public sealed partial class ToolsViewModel : PageViewModel
{
    private const string RepositoryAddress = "https://github.com/TwilightLemon/LemonNeo";
    private static readonly Assembly ApplicationAssembly = typeof(Program).Assembly;
    private readonly ISettingsScope _playbackSettings;
    private readonly IDisposable _playbackSubscription;
    [ObservableProperty] private IReadOnlyList<PluginDetail> _applicationDetails = [];
    private bool _closing;
    private string?[] _decoderIds = [];
    [ObservableProperty] private IReadOnlyList<string> _decoderOptions = [];
    [ObservableProperty] private IReadOnlyList<DecoderInformation> _decoders = [];
    [ObservableProperty] private IReadOnlyList<string> _playableNamespaces = [];
    [ObservableProperty] private IReadOnlyList<PluginDependencyGroup> _pluginDependencies = [];
    [ObservableProperty] private IReadOnlyList<PluginDetail> _pluginFailures = [];
    [ObservableProperty] private IReadOnlyList<PluginProviderGroup> _providerGroups = [];

    [ObservableProperty] private IReadOnlyList<string> _supportedNamespaces = [];

    public ToolsViewModel(DesktopContext context) : base(context, DesktopRoutes.Tools)
    {
        _playbackSettings = _host.Settings.ForPackage(PackageNames.Application);
        _playbackSubscription = _playbackSettings.Subscribe<PlaybackPreferences>(_ => RefreshDecoders());
        RefreshPluginInformation();
    }

    public string ApplicationName => "Lemon Neo";

    public string ApplicationVersion => ApplicationAssembly
                                            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                            ?.InformationalVersion.Split('+')[0]
                                        ?? ApplicationAssembly.GetName().Version?.ToString()
                                        ?? "—";

    public string CopyrightNotice => _text["CopyrightNotice"];

    public IReadOnlyList<DependencyInformation> ApplicationDependencies { get; } =
    [
        new("Avalonia.Desktop", "12.1.3", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        new("Avalonia.Themes.Fluent", "12.1.3", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        new("CommunityToolkit.Mvvm", "8.4.2", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("Irihi.Lingua", "1.0.0", "MIT", "https://github.com/irihitech/Irihi.Lingua"),
        new("Microsoft.AspNetCore.DataProtection.Extensions", "10.0.12", "MIT", "https://github.com/dotnet/dotnet"),
        new("Microsoft.Extensions.Caching.Memory", "10.0.12", "MIT", "https://github.com/dotnet/dotnet"),
        new("Microsoft.Extensions.DependencyInjection", "10.0.12", "MIT", "https://github.com/dotnet/dotnet"),
        new("Microsoft.Extensions.Http", "10.0.12", "MIT", "https://github.com/dotnet/dotnet"),
        new("Microsoft.Extensions.Logging", "10.0.12", "MIT", "https://github.com/dotnet/dotnet"),
        new("Microsoft.Extensions.Logging.Abstractions", "10.0.12", "MIT", "https://github.com/dotnet/dotnet"),
        new("Microsoft.Extensions.Logging.Debug", "10.0.12", "MIT", "https://github.com/dotnet/dotnet"),
        new("TagLibSharp", "2.3.0", "LGPL-2.1-only", "https://github.com/mono/taglib-sharp")
    ];

    public bool HasSupportedNamespaces => SupportedNamespaces.Count > 0;
    public bool HasPlayableNamespaces => PlayableNamespaces.Count > 0;
    public bool HasProviderGroups => ProviderGroups.Count > 0;
    public bool HasPluginFailures => PluginFailures.Count > 0;
    public bool HasDecoders => Decoders.Count > 0;

    public int SelectedDecoderIndex
    {
        get
        {
            var preferred = _playbackSettings.Get<PlaybackPreferences>().PreferredPlayerId;
            var index = Array.FindIndex(_decoderIds,
                id => string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase));
            return Math.Max(0, index);
        }
        set
        {
            if (_closing || value < 0 || value >= _decoderIds.Length) return;
            var id = _decoderIds[value];
            if (string.Equals(id, _playbackSettings.Get<PlaybackPreferences>().PreferredPlayerId,
                    StringComparison.OrdinalIgnoreCase)) return;
            _playbackSettings.Apply<PlaybackPreferences>(preferences => preferences with { PreferredPlayerId = id });
        }
    }

    public string PluginSummary => _text.Format("LoadedPluginSummary", _host.Plugins.Loaded.Count,
        _host.Get<IProviderRegistry>().Providers.Count, _host.Get<IPlayerRegistry>().Players.Count);

    private void RefreshPluginInformation()
    {
        var plugins = _host.Plugins.Loaded;
        var providers = _host.Get<IProviderRegistry>().Providers;
        var sources = _host.Get<IPlaybackSourceProviderRegistry>().Sources;
        var players = _host.Get<IPlayerRegistry>().Players;
        var groups = new Dictionary<string, List<PluginInformation>>
        {
            ["MusicProviders"] = [], ["PlaybackProviders"] = [], ["MetadataProviders"] = [],
            ["PlayerProviders"] = [], ["InteractivePlugins"] = [], ["OtherPlugins"] = []
        };
        foreach (var provider in providers)
        {
            var plugin =
                plugins.FirstOrDefault(p => p.ProviderIds.Contains(provider.Id, StringComparer.OrdinalIgnoreCase));
            var capabilities = new List<string>();

            void Capability(bool available, string key)
            {
                if (available) capabilities.Add(_text[key]);
            }

            Capability(provider is ISearchProvider, "CapabilitySearch");
            Capability(provider is ITrackLookupProvider, "CapabilityLookup");
            Capability(provider is IPlaylistProvider, "CapabilityPlaylist");
            Capability(provider is IPlaylistCollectionProvider, "CapabilityCollections");
            Capability(provider is IPlaybackProvider, "CapabilityPlayback");
            Capability(provider is IMetadataProvider, "CapabilityMetadata");
            Capability(provider is ILyricsProvider, "CapabilityLyrics");
            Capability(provider is IAuthenticationProvider, "CapabilityAuthentication");
            Capability(provider is IPlaylistExportProvider, "CapabilityPlaylistExport");
            var details = new List<PluginDetail>();
            if (provider is ISearchNamespaceProvider search)
                details.Add(new PluginDetail(_text["SearchSources"],
                    string.Join(", ", NormalizeNamespaces(search.SearchNamespaces))));
            if (provider is IMetadataProvider metadata)
                details.Add(new PluginDetail(_text["MetadataSources"],
                    string.Join(", ", NormalizeNamespaces(metadata.MetadataNamespaces))));
            if (provider is IPlaybackProvider playbackProvider && playbackProvider.AcceptedIdSchemes.Count > 0)
                details.Add(new PluginDetail(_text["AcceptedIds"],
                    string.Join(", ",
                        playbackProvider.AcceptedIdSchemes.Select(s => $"{s.Namespace}:{s.Kind ?? "*"}")
                            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))));
            details.AddRange(GetPluginDetails(plugin));
            var playable = sources.FirstOrDefault(s =>
                string.Equals(s.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase));
            var category = provider switch
            {
                IPlaybackProvider => "PlaybackProviders",
                IMetadataProvider => "MetadataProviders",
                ILyricsProvider when provider is not ISearchProvider and not IPlaylistProvider => "MetadataProviders",
                _ => "MusicProviders"
            };
            groups[category].Add(new PluginInformation(provider.Descriptor.Name, provider.Id,
                provider.Descriptor.Description,
                GetPluginSummary(plugin), capabilities, GetSupportedNamespaces(provider),
                playable is null ? [] : GetPlayableNamespaces(playable), details));
        }

        foreach (var player in players)
        {
            var plugin = plugins.FirstOrDefault(p => p.PlayerIds.Contains(player.Id, StringComparer.OrdinalIgnoreCase));
            groups["PlayerProviders"].Add(new PluginInformation(player.Descriptor.Name, player.Id,
                player.Descriptor.Description,
                GetPluginSummary(plugin), [_text["PlayerEngine"]], [], [],
                [
                    new PluginDetail(_text["SupportedFormats"], FormatExtensions(player.SupportedExtensions)),
                    .. GetPluginDetails(plugin)
                ]));
        }

        foreach (var plugin in plugins)
        {
            var hasProviders = providers.Any(p => plugin.ProviderIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase));
            var hasPlayers = players.Any(p => plugin.PlayerIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase));
            if (!plugin.IsInteractive && (hasProviders || hasPlayers)) continue;
            groups[plugin.IsInteractive ? "InteractivePlugins" : "OtherPlugins"].Add(new PluginInformation(
                plugin.Manifest.Name, plugin.Manifest.Id, "", GetPluginSummary(plugin),
                plugin.IsInteractive ? [_text["InteractivePlugin"]] : [], [], [], GetPluginDetails(plugin)));
        }

        ProviderGroups = groups.Where(g => g.Value.Count > 0).Select(g => new PluginProviderGroup(_text[g.Key],
            g.Value.OrderBy(p => p.Name, StringComparer.Create(_text.Culture, true))
                .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        PlayableNamespaces = NormalizeNamespaces(sources.SelectMany(GetPlayableNamespaces));
        SupportedNamespaces = NormalizeNamespaces(ProviderGroups.SelectMany(g => g.Items)
            .SelectMany(p => p.SupportedNamespaces).Concat(PlayableNamespaces));
        PluginFailures = _host.Plugins.LastReport.Failures.Select(f =>
                new PluginDetail(Path.GetFileName(Path.GetDirectoryName(f.ManifestPath)) ?? f.ManifestPath, f.Error))
            .ToArray();
        PluginDependencies = plugins
            .OrderBy(plugin => plugin.Manifest.Name, StringComparer.Create(_text.Culture, true))
            .ThenBy(plugin => plugin.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Select(plugin => new PluginDependencyGroup(
                plugin.Manifest.Name,
                plugin.Manifest.Version,
                plugin.Manifest.Dependencies.Select(dependency => new DependencyInformation(
                    dependency.Name,
                    dependency.Version,
                    dependency.License,
                    dependency.Website)).ToArray()))
            .ToArray();
        ApplicationDetails =
        [
            new PluginDetail(_text["Runtime"], RuntimeInformation.FrameworkDescription),
            new PluginDetail(_text["Architecture"], RuntimeInformation.ProcessArchitecture.ToString()),
            new PluginDetail(_text["License"], "GPL-3.0-only")
        ];
        foreach (var name in new[]
                 {
                     nameof(HasSupportedNamespaces), nameof(HasPlayableNamespaces), nameof(HasProviderGroups),
                     nameof(HasPluginFailures), nameof(PluginSummary)
                 }) OnPropertyChanged(name);
        RefreshDecoders();
    }

    [RelayCommand]
    private void OpenRepository()
    {
        Process.Start(new ProcessStartInfo(RepositoryAddress)
        {
            UseShellExecute = true
        });
    }

    private void RefreshDecoders()
    {
        if (_closing) return;
        var players = _host.Get<IPlayerRegistry>().Players.OrderBy(player => player.Id, StringComparer.Ordinal)
            .ToArray();
        var preferred = _playbackSettings.Get<PlaybackPreferences>().PreferredPlayerId;
        _decoderIds = [null, .. players.Select(player => player.Id)];
        DecoderOptions = [_text["AutomaticDecoder"], .. players.Select(player => player.Descriptor.Name)];
        Decoders = players.Select(player => new DecoderInformation(
            player.Descriptor.Name,
            player.Id,
            player.Descriptor.Description,
            player.SupportedExtensions.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            string.Equals(player.Id, preferred, StringComparison.OrdinalIgnoreCase))).ToArray();
        OnPropertyChanged(nameof(SelectedDecoderIndex));
        OnPropertyChanged(nameof(HasDecoders));
    }

    private string FormatExtensions(IEnumerable<string> extensions)
    {
        var values = extensions.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 0 ? _text["NoSupportedFormats"] : string.Join(", ", values);
    }

    private string GetPluginSummary(PluginInfo? plugin)
    {
        return plugin is null ? "" : _text.Format("PluginProvidedBy", plugin.Manifest.Name, plugin.Manifest.Version);
    }

    private IReadOnlyList<PluginDetail> GetPluginDetails(PluginInfo? plugin)
    {
        if (plugin is null) return [];
        var manifest = plugin.Manifest;
        return
        [
            new PluginDetail(_text["PluginId"], manifest.Id),
            new PluginDetail(_text["EntryAssembly"], manifest.EntryAssembly),
            new PluginDetail(_text["Platforms"],
                manifest.Platforms.Count == 0 ? _text["AllPlatforms"] : string.Join(", ", manifest.Platforms)),
            new PluginDetail(_text["HostVersion"], manifest.MinimumHostVersion),
            new PluginDetail(_text["Permissions"],
                manifest.Permissions.Count == 0 ? _text["NoPermissions"] : string.Join(", ", manifest.Permissions))
        ];
    }

    private static IReadOnlyList<string> GetSupportedNamespaces(IMusicProvider provider)
    {
        var namespaces = new List<string>();
        if (provider is ISearchNamespaceProvider search) namespaces.AddRange(search.SearchNamespaces);
        if (provider is IMetadataProvider metadata) namespaces.AddRange(metadata.MetadataNamespaces);
        if (provider is IPlaybackProvider playback)
            namespaces.AddRange(playback.AcceptedIdSchemes.Select(s => s.Namespace));
        return NormalizeNamespaces(namespaces);
    }

    private static IReadOnlyList<string> GetPlayableNamespaces(PlaybackSourceProviderRegistration source)
    {
        return NormalizeNamespaces(source.AcceptedIdSchemes.Select(s => s.Namespace));
    }

    private static IReadOnlyList<string> NormalizeNamespaces(IEnumerable<string> namespaces)
    {
        return namespaces
            .Where(ns => !string.IsNullOrWhiteSpace(ns)).Select(ns => ns.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public override Task ActivateAsync()
    {
        RefreshPluginInformation();
        return Task.CompletedTask;
    }

    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        RefreshPluginInformation();
    }

    public override async ValueTask DisposeAsync()
    {
        _closing = true;
        _playbackSubscription.Dispose();
        await base.DisposeAsync();
    }
}