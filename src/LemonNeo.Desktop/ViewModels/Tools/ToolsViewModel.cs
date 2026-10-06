using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Sync;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Application.Sync;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Tools;

public sealed partial class ToolsViewModel : PageViewModel
{
    private readonly PlaybackSessionService _session;
    public ToolsViewModel(DesktopContext context) : base(context, DesktopRoutes.Tools)
    {
        _session = _host.Get<PlaybackSessionService>();
        RefreshCommand = new(() => ExecuteAsync(async ct => { await _host.Get<LibraryService>().LoadAsync(ct); Context.RefreshLibrary(); }));
        RefreshSyncProviders();
        RefreshPluginInformation();
        SaveCommand = new(() => ExecuteAsync(async ct =>
        {
            await _host.Get<LibraryService>().SaveAsync(ct);
            await _session.CaptureStateAsync(ct);
            await _host.Settings.SaveAsync(ct);
            SetStatus("LibrarySaved");
        }));
        SyncCommand = new(action => SynchronizeAsync(action ?? "status"), _ => SelectedSyncProvider is not null);
    }

    [ObservableProperty] private string _syncDetails = "";
    private SyncStatus? _syncStatus;
    private SyncResult? _syncResult;
    private MergeResult? _syncMerge;
    [ObservableProperty] private ProviderOption? _selectedSyncProvider;
    partial void OnSelectedSyncProviderChanged(ProviderOption? value) => SyncCommand?.NotifyCanExecuteChanged();
    public ObservableCollection<ProviderOption> SyncProviders { get; } = [];
    public ObservableCollection<string> SyncConflicts { get; } = [];
    [ObservableProperty] private IReadOnlyList<string> _supportedNamespaces = [];
    [ObservableProperty] private IReadOnlyList<string> _playableNamespaces = [];
    [ObservableProperty] private IReadOnlyList<PluginProviderGroup> _providerGroups = [];
    [ObservableProperty] private IReadOnlyList<PluginDetail> _pluginFailures = [];
    public bool HasSupportedNamespaces => SupportedNamespaces.Count > 0;
    public bool HasPlayableNamespaces => PlayableNamespaces.Count > 0;
    public bool HasProviderGroups => ProviderGroups.Count > 0;
    public bool HasPluginFailures => PluginFailures.Count > 0;
    public string PluginSummary => _text.Format("LoadedPluginSummary", _host.Plugins.Loaded.Count,
        _host.Get<IProviderRegistry>().Providers.Count, _host.Get<IPlayerRegistry>().Players.Count);
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand<string> SyncCommand { get; }

    private void RefreshSyncProviders()
    {
        var selectedId = SelectedSyncProvider?.Id;
        SyncProviders.Clear();
        foreach (var id in _host.Get<SyncService>().ProviderIds) SyncProviders.Add(new(id, id));
        SelectedSyncProvider = SyncProviders.FirstOrDefault(p => p.Id == selectedId) ?? SyncProviders.FirstOrDefault();
    }

    public Task SynchronizeAsync(string action) => ExecuteAsync(async ct =>
    {
        var id = SelectedSyncProvider?.Id ?? throw new ArgumentException(_text["ProviderUnavailable"]);
        _syncStatus = null; _syncResult = null; _syncMerge = null;
        SyncConflicts.Clear();
        switch (action)
        {
            case "status":
                var status = await Task.Run(() => _host.Get<SyncService>().GetStatusAsync(id, ct), ct);
                _syncStatus = status;
                break;
            case "pull":
                var sync = _host.Get<SyncService>();
                var remote = await Task.Run(() => sync.FetchRemoteAsync(id, ct), ct);
                var pull = await sync.ApplyRemoteAsync(id, remote, ct);
                _syncResult = pull.ProviderResult;
                if (!pull.ProviderResult.Success) throw new InvalidOperationException(pull.ProviderResult.Message);
                if (pull.MergeResult is { } merge)
                {
                    _syncMerge = merge;
                    Context.RefreshLibrary();
                }
                break;
            case "push":
                await _host.Get<LibraryService>().SaveAsync(ct);
                var push = await Task.Run(() => _host.Get<SyncService>().PushRemoteAsync(id, ct), ct);
                _syncResult = push;
                if (!push.Success) throw new InvalidOperationException(push.Message);
                break;
            default: throw new ArgumentException(_text["SyncActionInvalid"]);
        }
        RefreshSyncText();
        SetStatus("SyncCompleted");
    });

    private void RefreshSyncText()
    {
        if (_syncStatus is { } status) SyncDetails = $"{_text[$"Sync{status.State}"]}\n" +
            _text.Format("SyncRevisions", status.LocalRevision ?? "—", status.RemoteRevision ?? "—") + $"\n{status.Message}";
        else if (_syncResult is { } result) SyncDetails = result.Message + "\n" + _text.Format("SyncRevisions", result.LocalRevision ?? "—", result.RemoteRevision ?? "—");
        if (_syncMerge is { } merge)
        {
            SyncDetails += "\n" + _text.Format("SyncMergeSummary", merge.AddedPlaylistItems, merge.ChangedPlaylists, merge.Conflicts.Count);
            SyncConflicts.Clear();
            foreach (var conflict in merge.Conflicts) SyncConflicts.Add($"{_text[$"Conflict{conflict.Type}"]}: {conflict.Path}");
        }
    }

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
            var plugin = plugins.FirstOrDefault(p => p.ProviderIds.Contains(provider.Id, StringComparer.OrdinalIgnoreCase));
            var capabilities = new List<string>();
            void Capability(bool available, string key) { if (available) capabilities.Add(_text[key]); }
            Capability(provider is ISearchProvider, "CapabilitySearch"); Capability(provider is ITrackLookupProvider, "CapabilityLookup");
            Capability(provider is ITrackIdResolverProvider, "CapabilityResolveIds"); Capability(provider is IPlaylistProvider, "CapabilityPlaylist");
            Capability(provider is IPlaylistCollectionProvider, "CapabilityCollections"); Capability(provider is IPlaybackProvider, "CapabilityPlayback");
            Capability(provider is IMetadataProvider, "CapabilityMetadata"); Capability(provider is ILyricsProvider, "CapabilityLyrics");
            Capability(provider is IAuthenticationProvider, "CapabilityAuthentication"); Capability(provider is IFolderImportProvider, "CapabilityFolder");
            Capability(provider is IPlaylistExportProvider, "CapabilityPlaylistExport");
            var details = new List<PluginDetail>();
            if (provider is ISearchNamespaceProvider search) details.Add(new(_text["SearchSources"], string.Join(", ", NormalizeNamespaces(search.SearchNamespaces))));
            if (provider is IMetadataProvider metadata) details.Add(new(_text["MetadataSources"], string.Join(", ", NormalizeNamespaces(metadata.MetadataNamespaces))));
            if (provider is ITrackIdResolverProvider resolver)
                details.Add(new(_text["AcceptedIds"], string.Join(", ", resolver.AcceptedIdSchemes.Select(s => $"{s.Namespace}:{s.Kind ?? "*"}").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))));
            details.AddRange(GetPluginDetails(plugin));
            var playable = sources.FirstOrDefault(s => string.Equals(s.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase));
            var category = provider switch
            {
                IPlaybackProvider => "PlaybackProviders",
                IMetadataProvider => "MetadataProviders",
                ILyricsProvider when provider is not ISearchProvider and not IPlaylistProvider => "MetadataProviders",
                _ => "MusicProviders"
            };
            groups[category].Add(new(provider.Descriptor.Name, provider.Id, provider.Descriptor.Description,
                GetPluginSummary(plugin), capabilities, GetSupportedNamespaces(provider),
                playable is null ? [] : GetPlayableNamespaces(playable), details));
        }
        foreach (var player in players)
        {
            var plugin = plugins.FirstOrDefault(p => p.PlayerIds.Contains(player.Id, StringComparer.OrdinalIgnoreCase));
            groups["PlayerProviders"].Add(new(player.Descriptor.Name, player.Id, player.Descriptor.Description,
                GetPluginSummary(plugin), [_text["PlayerEngine"]], [], [],
                [new(_text["Priority"], player.Priority.ToString(_text.Culture)), .. GetPluginDetails(plugin)]));
        }
        foreach (var plugin in plugins)
        {
            var hasProviders = providers.Any(p => plugin.ProviderIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase));
            var hasPlayers = players.Any(p => plugin.PlayerIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase));
            if (!plugin.IsInteractive && (hasProviders || hasPlayers)) continue;
            groups[plugin.IsInteractive ? "InteractivePlugins" : "OtherPlugins"].Add(new(
                plugin.Manifest.Name, plugin.Manifest.Id, "", GetPluginSummary(plugin),
                plugin.IsInteractive ? [_text["InteractivePlugin"]] : [], [], [], GetPluginDetails(plugin)));
        }
        ProviderGroups = groups.Where(g => g.Value.Count > 0).Select(g => new PluginProviderGroup(_text[g.Key],
            g.Value.OrderBy(p => p.Name, StringComparer.Create(_text.Culture, true)).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        PlayableNamespaces = NormalizeNamespaces(sources.SelectMany(GetPlayableNamespaces));
        SupportedNamespaces = NormalizeNamespaces(ProviderGroups.SelectMany(g => g.Items).SelectMany(p => p.SupportedNamespaces).Concat(PlayableNamespaces));
        PluginFailures = _host.Plugins.LastReport.Failures.Select(f => new PluginDetail(Path.GetFileName(Path.GetDirectoryName(f.ManifestPath)) ?? f.ManifestPath, f.Error)).ToArray();
        foreach (var name in new[] { nameof(HasSupportedNamespaces), nameof(HasPlayableNamespaces), nameof(HasProviderGroups), nameof(HasPluginFailures), nameof(PluginSummary) }) OnPropertyChanged(name);
    }

    private string GetPluginSummary(PluginInfo? plugin) => plugin is null ? "" :
        _text.Format("PluginProvidedBy", plugin.Manifest.Name, plugin.Manifest.Version);

    private IReadOnlyList<PluginDetail> GetPluginDetails(PluginInfo? plugin)
    {
        if (plugin is null) return [];
        var manifest = plugin.Manifest;
        return [new(_text["PluginId"], manifest.Id), new(_text["EntryAssembly"], manifest.EntryAssembly),
            new(_text["Platforms"], manifest.Platforms.Count == 0 ? _text["AllPlatforms"] : string.Join(", ", manifest.Platforms)),
            new(_text["HostVersion"], manifest.MinimumHostVersion),
            new(_text["Permissions"], manifest.Permissions.Count == 0 ? _text["NoPermissions"] : string.Join(", ", manifest.Permissions))];
    }

    private static IReadOnlyList<string> GetSupportedNamespaces(IMusicProvider provider)
    {
        var namespaces = new List<string>();
        if (provider is ISearchNamespaceProvider search) namespaces.AddRange(search.SearchNamespaces);
        if (provider is IMetadataProvider metadata) namespaces.AddRange(metadata.MetadataNamespaces);
        if (provider is ITrackIdResolverProvider resolver) namespaces.AddRange(resolver.AcceptedIdSchemes.Select(s => s.Namespace));
        return NormalizeNamespaces(namespaces);
    }

    private static IReadOnlyList<string> GetPlayableNamespaces(PlaybackSourceProviderRegistration source) =>
        NormalizeNamespaces(source.AcceptedIdSchemes.Select(s => s.Namespace));

    private static IReadOnlyList<string> NormalizeNamespaces(IEnumerable<string> namespaces) => namespaces
        .Where(ns => !string.IsNullOrWhiteSpace(ns)).Select(ns => ns.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public override Task ActivateAsync()
    {
        RefreshSyncProviders();
        RefreshPluginInformation();
        return Task.CompletedTask;
    }
    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties(); RefreshSyncText(); RefreshPluginInformation();
    }
}
