using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Desktop.ViewModels;

public sealed partial class OnlineViewModel : PageViewModel
{
    public OnlineViewModel(DesktopContext context) : base(context, DesktopRoutes.Online)
    {
        RemotePlaylistsCommand = new(LoadRemotePlaylistsAsync);
        ImportRemoteCommand = new(() => CanImportRemote && SelectedRemotePlaylist is { Id: { } id } row
            ? ImportRemoteAsync(row.ProviderId, id) : Task.CompletedTask);
        RemotePlaylists.CollectionChanged += (_, _) => RaiseRemotePlaylistProperties();
        PlaylistCategories.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCategoryMenu));
        RefreshProviderOptions();
        Context.AuthenticationChanged += OnAuthenticationChanged;
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RemoteEmptyTitle)), NotifyPropertyChangedFor(nameof(RemoteEmptyDescription))]
    private bool _hasLoadedRemotePlaylists;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanImportRemote)), NotifyPropertyChangedFor(nameof(HasSelectedRemotePlaylist)),
        NotifyPropertyChangedFor(nameof(SelectedPlaylistSummary))] private RemotePlaylistRow? _selectedRemotePlaylist;
    private ProviderOption? _selectedService;
    private RemotePlaylistCategory? _selectedCategory;
    private readonly Dictionary<string, string> _playlistSelections = [];
    private bool _activated;
    public ProviderOption? SelectedService
    {
        get => _selectedService;
        set
        {
            var changedProvider = _selectedService?.Id != value?.Id;
            if (SetProperty(ref _selectedService, value))
            {
                if (changedProvider)
                {
                    ResetRemotePlaylists();
                    SetError(null);
                    SetStatus("Ready");
                }
                RaiseServiceProperties();
                if (changedProvider && _activated && CanBrowseRemote) RemotePlaylistsCommand.Execute(null);
            }
        }
    }
    public ObservableCollection<ProviderOption> Services { get; } = [];
    public ObservableCollection<RemotePlaylistCategory> PlaylistCategories { get; } = [];
    public ObservableCollection<RemotePlaylistRow> RemotePlaylists { get; } = [];
    public bool HasCategoryMenu => PlaylistCategories.Count > 1;
    public RemotePlaylistCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (Equals(_selectedCategory, value)) return;
            RememberPlaylistSelection();
            SetProperty(ref _selectedCategory, value);
            RemotePlaylists.Clear();
            if (value is not null)
                foreach (var row in value.Playlists) RemotePlaylists.Add(row);
            var selectedId = value is not null ? _playlistSelections.GetValueOrDefault(value.Id) : null;
            SelectedRemotePlaylist = RemotePlaylists.FirstOrDefault(row => row.CanImport && row.Id == selectedId)
                ?? RemotePlaylists.FirstOrDefault(row => row.CanImport);
            RaiseRemotePlaylistProperties();
        }
    }
    public bool HasServices => Services.Count > 0;
    private IMusicProvider? Service => SelectedService?.Id is { } id ? _runtime.Get<IProviderRegistry>().Get(id) : null;
    public bool SupportsPlaylists => Service is IPlaylistProvider;
    public bool HasRemotePlaylists => RemotePlaylists.Any(row => row.CanImport);
    public bool HasSelectedRemotePlaylist => SelectedRemotePlaylist is { CanImport: true };
    public string SelectedPlaylistSummary => SelectedRemotePlaylist is { TrackCount: not "—" } row
        ? _text.Format("RemoteTrackCount", row.TrackCount) : _text["RemoteTrackCountUnknown"];
    public string RemoteEmptyTitle => !SupportsPlaylists ? _text["NoPlaylistServices"]
        : HasLoadedRemotePlaylists ? _text[HasCategoryMenu && SelectedCategory is not null ? "EmptyCollection" : "NoRemotePlaylistsFound"]
        : _text["EmptyRemote"];
    public string RemoteEmptyDescription => !SupportsPlaylists ? _text["NoPlaylistServicesDescription"]
        : HasLoadedRemotePlaylists ? _text[HasCategoryMenu && SelectedCategory is not null ? "EmptyCollectionDescription" : "NoRemotePlaylistsFoundDescription"]
        : _text["EmptyRemoteDescription"];
    public bool CanBrowseRemote => CanInteract && SupportsPlaylists;
    public bool CanImportRemote => CanInteract && SelectedRemotePlaylist is { CanImport: true } row
        && row.ProviderId == SelectedService?.Id && RemotePlaylists.Contains(row);
    public AsyncRelayCommand RemotePlaylistsCommand { get; }
    public AsyncRelayCommand ImportRemoteCommand { get; }
    private void RefreshProviderOptions()
    {
        var providers = _runtime.Get<IProviderRegistry>().Providers;
        var options = providers.Where(p => p is IPlaylistProvider)
            .Select(p => new ProviderOption(p.Id, p.Descriptor.Name)).ToArray();
        var selectedId = SelectedService?.Id
            ?? providers.FirstOrDefault(p => p is IPlaylistProvider and IAuthenticationProvider { State.IsAuthenticated: true })?.Id
            ?? options.FirstOrDefault()?.Id;
        // Keep tab selection and page state when revisiting or changing language.
        if (!Services.SequenceEqual(options))
        {
            Services.Clear(); foreach (var option in options) Services.Add(option);
        }
        SelectedService = Services.FirstOrDefault(option => option.Id == selectedId) ?? Services.FirstOrDefault();
        OnPropertyChanged(nameof(HasServices));
        RaiseServiceProperties();
        RaiseRemotePlaylistProperties();
    }
    public Task LoadRemotePlaylistsAsync() => ExecuteAsync(async ct =>
    {
        if (SelectedService?.Id is not { } id || Service is not IPlaylistProvider provider)
            throw new ArgumentException(_text["ProviderUnavailable"]);
        var categories = new List<RemotePlaylistCategory>();
        var selectedCategoryId = SelectedCategory?.Id;
        await Task.Run(async () =>
        {
            if (provider is IPlaylistCollectionProvider groups)
            {
                await foreach (var collection in groups.GetUserPlaylistCollectionsAsync(ct))
                {
                    var rows = collection.Playlists.Select(playlist => new RemotePlaylistRow(id, playlist.Id, playlist.Name,
                        collection.Name, playlist.TrackCount?.ToString(_text.Culture) ?? "—")).ToArray();
                    categories.Add(new(collection.Id, collection.Name, rows));
                }
            }
            else
            {
                var rows = new List<RemotePlaylistRow>();
                await foreach (var playlist in provider.GetUserPlaylistsAsync(ct))
                    rows.Add(new(id, playlist.Id, playlist.Name, _text["RemotePlaylists"], playlist.TrackCount?.ToString(_text.Culture) ?? "—"));
                categories.Add(new("", _text["RemotePlaylists"], rows));
            }
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (SelectedService?.Id != id) return;
        RememberPlaylistSelection();
        PlaylistCategories.Clear();
        foreach (var category in categories) PlaylistCategories.Add(category);
        SelectedCategory = PlaylistCategories.FirstOrDefault(category => category.Id == selectedCategoryId)
            ?? PlaylistCategories.FirstOrDefault(category => category.Playlists.Any(row => row.CanImport))
            ?? PlaylistCategories.FirstOrDefault();
        HasLoadedRemotePlaylists = true;
    });

    private Task ImportRemoteAsync(string providerId, string id) => ExecuteAsync(async ct =>
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(_text["RemotePlaylistUnavailable"]);
        var importer = _runtime.Get<ImportService>();
        var remote = await Task.Run(() => importer.GetRemotePlaylistAsync(providerId, id.Trim(), ct), ct);
        ct.ThrowIfCancellationRequested();
        var playlist = importer.ImportRemote(remote);
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        Context.RefreshLibrary();
        SetStatus("RemoteImported", playlist.Name, playlist.Items.Count);
    });

    public override async Task ActivateAsync()
    {
        _activated = true;
        RefreshProviderOptions();
        RaiseServiceProperties();
        if (!HasLoadedRemotePlaylists && CanBrowseRemote) await RemotePlaylistsCommand.ExecuteAsync(null);
    }
    private void ResetRemotePlaylists()
    {
        PlaylistCategories.Clear();
        SelectedCategory = null;
        _playlistSelections.Clear();
        RemotePlaylists.Clear();
        SelectedRemotePlaylist = null;
        HasLoadedRemotePlaylists = false;
    }
    private void RememberPlaylistSelection()
    {
        if (SelectedCategory is { } category && SelectedRemotePlaylist is { CanImport: true, Id: { } id })
            _playlistSelections[category.Id] = id;
    }
    private void RaiseServiceProperties()
    {
        foreach (var name in new[] { nameof(SupportsPlaylists), nameof(CanBrowseRemote), nameof(CanImportRemote) }) OnPropertyChanged(name);
    }
    private void RaiseRemotePlaylistProperties()
    {
        foreach (var name in new[] { nameof(HasRemotePlaylists), nameof(CanImportRemote), nameof(RemoteEmptyTitle),
            nameof(RemoteEmptyDescription), nameof(SelectedPlaylistSummary) }) OnPropertyChanged(name);
    }
    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        RaiseServiceProperties();
    }
    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties(); RefreshProviderOptions(); RaiseInteractionProperties();
        if (Service is not IPlaylistCollectionProvider)
            for (var i = 0; i < PlaylistCategories.Count; i++)
            {
                var category = PlaylistCategories[i];
                var name = _text["RemotePlaylists"];
                if (category.Name == name && category.Playlists.All(row => row.Collection == name)) continue;
                var selected = ReferenceEquals(category, SelectedCategory);
                var updated = category with { Name = name, Playlists = category.Playlists.Select(row => row with { Collection = name }).ToArray() };
                PlaylistCategories[i] = updated;
                if (selected) SelectedCategory = updated;
            }
        RaiseRemotePlaylistProperties();
    }
    private void OnAuthenticationChanged(string providerId)
    {
        if (SelectedService?.Id != providerId) return;
        if (IsBusy) CancelOperationCommand.Execute(null);
        ResetRemotePlaylists();
        SetError(null);
        SetStatus("Ready");
    }
    public override async ValueTask DisposeAsync()
    {
        Context.AuthenticationChanged -= OnAuthenticationChanged;
        await base.DisposeAsync();
    }
}

public sealed record RemotePlaylistCategory(string Id, string Name, IReadOnlyList<RemotePlaylistRow> Playlists);
