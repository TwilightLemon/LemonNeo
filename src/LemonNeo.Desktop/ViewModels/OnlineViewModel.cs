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
        LoginCommand = new(LoginAsync); LogoutCommand = new(LogoutAsync);
        RemotePlaylistsCommand = new(LoadRemotePlaylistsAsync);
        ImportRemoteCommand = new(() => SelectedRemotePlaylist is { Id: { } id } row ? ImportRemoteAsync(row.ProviderId, id) : Task.CompletedTask);
        ImportRemoteIdCommand = new(() => SelectedPlaylistProvider?.Id is { } provider ? ImportRemoteAsync(provider, RemotePlaylistId) : Task.CompletedTask);
        RefreshProviderOptions();
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanLogin))] private string _cookie = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanImportRemoteId))] private string _remotePlaylistId = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanBrowseRemote)), NotifyPropertyChangedFor(nameof(CanImportRemoteId))] private ProviderOption? _selectedPlaylistProvider;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanImportRemote))] private RemotePlaylistRow? _selectedRemotePlaylist;
    private ProviderOption? _selectedAuthenticationProvider;
    public ProviderOption? SelectedAuthenticationProvider
    {
        get => _selectedAuthenticationProvider;
        set
        {
            var changedProvider = _selectedAuthenticationProvider?.Id != value?.Id;
            if (SetProperty(ref _selectedAuthenticationProvider, value))
            {
                if (changedProvider) Cookie = "";
                OnPropertyChanged(nameof(AuthenticationStatus));
                OnPropertyChanged(nameof(AuthenticationAccount));
                OnPropertyChanged(nameof(CanLogin)); OnPropertyChanged(nameof(CanLogout));
            }
        }
    }
    public ObservableCollection<ProviderOption> AuthenticationProviders { get; } = [];
    public ObservableCollection<ProviderOption> PlaylistProviders { get; } = [];
    public ObservableCollection<RemotePlaylistRow> RemotePlaylists { get; } = [];
    public bool CanLogin => CanInteract && SelectedAuthenticationProvider?.Id is not null && !string.IsNullOrWhiteSpace(Cookie);
    public bool CanLogout => CanInteract && SelectedAuthenticationProvider?.Id is { } id &&
        _runtime.Get<IProviderRegistry>().Get(id) is IAuthenticationProvider { State.IsAuthenticated: true };
    public bool CanBrowseRemote => CanInteract && SelectedPlaylistProvider?.Id is not null;
    public bool CanImportRemoteId => CanBrowseRemote && !string.IsNullOrWhiteSpace(RemotePlaylistId);
    public bool CanImportRemote => CanInteract && SelectedRemotePlaylist is { CanImport: true };
    public string AuthenticationStatus => SelectedAuthenticationProvider?.Id is { } id &&
        _runtime.Get<IProviderRegistry>().Get(id) is IAuthenticationProvider { State.IsAuthenticated: true }
        ? _text["Authenticated"] : _text["NotAuthenticated"];
    public string AuthenticationAccount => SelectedAuthenticationProvider?.Id is { } id &&
        _runtime.Get<IProviderRegistry>().Get(id) is IAuthenticationProvider { State.IsAuthenticated: true } provider
        ? $"{provider.State.AccountName ?? provider.State.UserId ?? id}  ·  {provider.State.UserId ?? "—"}" : "";

    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }
    public AsyncRelayCommand RemotePlaylistsCommand { get; }
    public AsyncRelayCommand ImportRemoteCommand { get; }
    public AsyncRelayCommand ImportRemoteIdCommand { get; }
    private void RefreshProviderOptions()
    {
        var providers = _runtime.Get<IProviderRegistry>().Providers;
        static ProviderOption Option(IMusicProvider p) => new(p.Id, $"{p.Descriptor.Name} ({p.Id})");
        static ProviderOption? Replace(ObservableCollection<ProviderOption> collection, IEnumerable<ProviderOption> values, ProviderOption? selection)
        {
            collection.Clear(); foreach (var value in values) collection.Add(value);
            return collection.FirstOrDefault(o => o.Id == selection?.Id) ?? collection.FirstOrDefault();
        }
        SelectedAuthenticationProvider = Replace(AuthenticationProviders, providers.Where(p => p is IAuthenticationProvider).Select(Option), SelectedAuthenticationProvider);
        SelectedPlaylistProvider = Replace(PlaylistProviders, providers.Where(p => p is IPlaylistProvider).Select(Option), SelectedPlaylistProvider);
    }
    public Task LoginAsync() => ExecuteAsync(async ct =>
    {
        try
        {
            var provider = GetAuthenticationProvider();
            if (!(await Task.Run(() => provider.GetAvailableMethodsAsync(ct), ct)).Contains(AuthMethod.Cookie))
                throw new ArgumentException(_text["CookieUnsupported"]);
            if (string.IsNullOrWhiteSpace(Cookie)) throw new ArgumentException(_text["CookieRequired"]);
            await Task.Run(() => provider.AuthenticateAsync(new(AuthMethod.Cookie, new Dictionary<string, string> { ["cookie"] = Cookie.Trim() }), ct), ct);
            if (PlaylistProviders.FirstOrDefault(p => p.Id == SelectedAuthenticationProvider?.Id) is { } playlists)
                SelectedPlaylistProvider = playlists;
            SetStatus("LoginCompleted");
        }
        finally
        {
            Cookie = "";
            OnPropertyChanged(nameof(AuthenticationStatus)); OnPropertyChanged(nameof(AuthenticationAccount));
        }
    });

    public Task LogoutAsync() => ExecuteAsync(async ct =>
    {
        await Task.Run(() => GetAuthenticationProvider().SignOutAsync(ct), ct);
        Cookie = "";
        RemotePlaylists.Clear();
        SelectedRemotePlaylist = null;
        OnPropertyChanged(nameof(AuthenticationStatus)); OnPropertyChanged(nameof(AuthenticationAccount));
        SetStatus("LogoutCompleted");
    });

    private IAuthenticationProvider GetAuthenticationProvider() => SelectedAuthenticationProvider?.Id is { } id &&
        _runtime.Get<IProviderRegistry>().Get(id) is IAuthenticationProvider provider
        ? provider : throw new ArgumentException(_text["ProviderUnavailable"]);

    partial void OnSelectedPlaylistProviderChanged(ProviderOption? oldValue, ProviderOption? newValue)
    {
        if (oldValue?.Id == newValue?.Id) return;
        RemotePlaylists.Clear(); SelectedRemotePlaylist = null;
    }

    public Task LoadRemotePlaylistsAsync() => ExecuteAsync(async ct =>
    {
        if (SelectedPlaylistProvider?.Id is not { } id || _runtime.Get<IProviderRegistry>().Get(id) is not IPlaylistProvider provider)
            throw new ArgumentException(_text["ProviderUnavailable"]);
        var rows = new List<RemotePlaylistRow>();
        await Task.Run(async () =>
        {
            if (provider is IPlaylistCollectionProvider groups)
            {
                await foreach (var collection in groups.GetUserPlaylistCollectionsAsync(ct))
                {
                    if (collection.Playlists.Count == 0) rows.Add(new(id, null, _text["EmptyCollection"], collection.Name, "—"));
                    foreach (var playlist in collection.Playlists)
                        rows.Add(new(id, playlist.Id, playlist.Name, collection.Name, playlist.TrackCount?.ToString(_text.Culture) ?? "—"));
                }
            }
            else
            {
                await foreach (var playlist in provider.GetUserPlaylistsAsync(ct))
                    rows.Add(new(id, playlist.Id, playlist.Name, _text["RemotePlaylists"], playlist.TrackCount?.ToString(_text.Culture) ?? "—"));
            }
        }, ct);
        ct.ThrowIfCancellationRequested();
        RemotePlaylists.Clear();
        foreach (var row in rows) RemotePlaylists.Add(row);
        SelectedRemotePlaylist = RemotePlaylists.FirstOrDefault(r => r.CanImport);
        SetStatus("RemotePlaylistsLoaded", rows.Count(r => r.CanImport));
    });

    public Task ImportRemoteAsync(string providerId, string id) => ExecuteAsync(async ct =>
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(_text["RemoteIdRequired"]);
        var importer = _runtime.Get<ImportService>();
        var remote = await Task.Run(() => importer.GetRemotePlaylistAsync(providerId, id.Trim(), ct), ct);
        ct.ThrowIfCancellationRequested();
        var playlist = importer.ImportRemote(remote);
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        Context.RefreshLibrary();
        SetStatus("RemoteImported", playlist.Name, playlist.Items.Count);
    });

    public override Task ActivateAsync() { RefreshProviderOptions(); return Task.CompletedTask; }
    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        foreach (var name in new[] { nameof(CanLogin), nameof(CanLogout), nameof(CanBrowseRemote), nameof(CanImportRemoteId), nameof(CanImportRemote),
            nameof(AuthenticationStatus), nameof(AuthenticationAccount) }) OnPropertyChanged(name);
    }
    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties(); RefreshProviderOptions(); RaiseInteractionProperties();
        for (var i = 0; i < RemotePlaylists.Count; i++)
            if (!RemotePlaylists[i].CanImport) RemotePlaylists[i] = RemotePlaylists[i] with { Name = _text["EmptyCollection"] };
    }
}
