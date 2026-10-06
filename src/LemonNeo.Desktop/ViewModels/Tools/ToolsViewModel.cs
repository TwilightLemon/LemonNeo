using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Sync;
using LemonNeo.Application;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Application.Sync;
using LemonNeo.Core.Lyrics;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Tools;

public sealed partial class ToolsViewModel : PageViewModel
{
    private readonly PlaybackSessionService _session;
    public ToolsViewModel(DesktopContext context, DesktopNavigation navigation) : base(context, DesktopRoutes.Tools)
    {
        _session = _host.Get<PlaybackSessionService>();
        NavigateCommand = navigation.NavigateCommand;
        RefreshCommand = new(() => ExecuteAsync(async ct => { await _host.Get<LibraryService>().LoadAsync(ct); Context.RefreshLibrary(); }));
        RefreshSyncProviders();
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
    public string Version => $"Lemon Neo {typeof(ToolsViewModel).Assembly.GetName().Version?.ToString(3)}";
    public string PluginPath => _host.Get<DesktopOptions>().PluginDirectory;
    public string DataPath => _host.Paths.DataDirectory;
    public string Diagnostics => BuildDiagnostics();
    public string PathDetails => string.Join(Environment.NewLine, new[]
    {
        $"{_text["ConfigurationPath"]}: {ConfigurationPath}", $"{_text["AppSettingsPath"]}: {_host.Paths.GetPackageConfigurationDirectory(PackageNames.Application)}",
        $"{_text["DataPath"]}: {DataPath}", $"{_text["HistoryPath"]}: {_host.Paths.GetEntityConfigurationFile(PackageNames.Console, "CommandHistoryState")}",
        $"{_text["CachePath"]}: {CachePath}", $"{_text["LyricsCachePath"]}: {Path.Combine(CachePath, "Lyrics")}",
        $"{_text["ImageCachePath"]}: {_host.Get<ImageCache>().DirectoryPath}",
        $"{_text["LibraryPath"]}: {LibraryPath}", $"{_text["PluginPath"]}: {PluginPath}"
    });
    public string MetadataNamespaces => string.Join(", ", _host.Get<IProviderRegistry>().Providers.OfType<IMetadataProvider>()
        .SelectMany(p => p.MetadataNamespaces).Distinct(StringComparer.OrdinalIgnoreCase).Order());

    public string LibrarySummary => Context.LibrarySummary;
    public string ConfigurationPath => _host.Paths.ConfigurationDirectory;
    public string CachePath => _host.Paths.CacheDirectory;
    public string LibraryPath => _host.Get<DesktopOptions>().LibraryDirectory;
    public AsyncRelayCommand<string> NavigateCommand { get; }
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

    private string BuildDiagnostics()
    {
        var text = new StringBuilder(Version).AppendLine().AppendLine(LibrarySummary).AppendLine();
        var loader = _host.Plugins;
        foreach (var plugin in loader.Loaded)
        {
            var manifest = plugin.Manifest;
            text.AppendLine($"{manifest.Name} {manifest.Version} ({manifest.Id})")
                .AppendLine($"{_text["EntryAssembly"]}: {manifest.EntryAssembly}")
                .AppendLine($"{_text["Platforms"]}: {string.Join(", ", manifest.Platforms)}")
                .AppendLine($"{_text["HostVersion"]}: {manifest.MinimumHostVersion}")
                .AppendLine($"{_text["Permissions"]}: {string.Join(", ", manifest.Permissions)}");
            if (plugin.IsInteractive) text.AppendLine(_text["InteractivePlugin"]);
        }
        foreach (var provider in _host.Get<IProviderRegistry>().Providers)
        {
            text.AppendLine().AppendLine($"{provider.Descriptor.Name} ({provider.Id})").AppendLine(provider.Descriptor.Description);
            var capabilities = new List<string>();
            void Capability(bool available, string key) { if (available) capabilities.Add(_text[key]); }
            Capability(provider is ISearchProvider, "CapabilitySearch"); Capability(provider is ITrackLookupProvider, "CapabilityLookup");
            Capability(provider is ITrackIdResolverProvider, "CapabilityResolveIds"); Capability(provider is IPlaylistProvider, "CapabilityPlaylist");
            Capability(provider is IPlaylistCollectionProvider, "CapabilityCollections"); Capability(provider is IPlaybackProvider, "CapabilityPlayback");
            Capability(provider is IMetadataProvider, "CapabilityMetadata"); Capability(provider is ILyricsProvider, "CapabilityLyrics");
            Capability(provider is IAuthenticationProvider, "CapabilityAuthentication"); Capability(provider is IFolderImportProvider, "CapabilityFolder");
            text.AppendLine($"{_text["Capabilities"]}: {string.Join(", ", capabilities)}");
            if (provider is ISearchNamespaceProvider search) text.AppendLine($"{_text["SearchSources"]}: {string.Join(", ", search.SearchNamespaces)}");
            if (provider is IMetadataProvider metadata) text.AppendLine($"{_text["MetadataSources"]}: {string.Join(", ", metadata.MetadataNamespaces)}");
            if (provider is ITrackIdResolverProvider resolver)
                text.AppendLine($"{_text["AcceptedIds"]}: {string.Join(", ", resolver.AcceptedIdSchemes.Select(s => $"{s.Namespace}:{s.Kind ?? "-"}"))}");
        }
        foreach (var player in _host.Get<IPlayerRegistry>().Players)
            text.AppendLine().AppendLine($"{_text["PlayerEngine"]}: {player.Descriptor.Name} ({player.Id}) · {_text["Priority"]} {player.Priority}")
                .AppendLine(player.Descriptor.Description);
        foreach (var failure in loader.LastReport.Failures) text.AppendLine().AppendLine($"{failure.ManifestPath}: {failure.Error}");
        return text.ToString();
    }

    public override Task ActivateAsync()
    {
        RefreshSyncProviders();
        return Task.CompletedTask;
    }
    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties(); RefreshSyncText();
        foreach (var name in new[] { nameof(Diagnostics), nameof(PathDetails), nameof(LibrarySummary), nameof(MetadataNamespaces) }) OnPropertyChanged(name);
    }
}
