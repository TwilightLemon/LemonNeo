using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Accounts;

public sealed partial class AccountsViewModel : PageViewModel
{
    private readonly ArtworkService _artwork;
    private readonly CancellationTokenSource _avatarLifetime = new();
    private readonly List<Task> _avatarTasks = [];
    private Bitmap? _accountAvatar;
    private CancellationTokenSource? _avatarCancellation;
    private (string? ProviderId, string? UserId, Uri? Uri) _avatarIdentity;
    private bool _closing;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanLogin))]
    private string _cookie = "";

    [ObservableProperty] private bool _isCookieEditorOpen = true;
    private ProviderOption? _selectedService;

    public AccountsViewModel(DesktopContext context) : base(context, DesktopRoutes.Accounts)
    {
        _artwork = _host.Get<ArtworkService>();
        LoginCommand = new AsyncRelayCommand(LoginAsync);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        RefreshServices();
    }

    public ProviderOption? SelectedService
    {
        get => _selectedService;
        set
        {
            var changedService = _selectedService?.Id != value?.Id;
            if (!SetProperty(ref _selectedService, value)) return;
            if (changedService)
            {
                Cookie = "";
                IsCookieEditorOpen = !IsAuthenticated;
                SetError(null);
                SetStatus(null);
            }

            RaiseAccountProperties();
        }
    }

    public ObservableCollection<ProviderOption> Services { get; } = [];
    public bool HasServices => Services.Count > 0;

    private IAuthenticationProvider? Provider => SelectedService?.Id is { } id
        ? _host.Get<IProviderRegistry>().Get(id) as IAuthenticationProvider
        : null;

    private PluginInfo? ProviderPlugin => SelectedService?.Id is { } id
        ? _host.Plugins.Loaded.FirstOrDefault(plugin =>
            plugin.ProviderIds.Contains(id, StringComparer.OrdinalIgnoreCase))
        : null;

    public bool HasPluginAttribution => ProviderPlugin is not null;

    public string PluginAttribution => ProviderPlugin is { } plugin
        ? _text.Format(LanguageManager.Keys.AuthenticationProvidedByPlugin, plugin.Manifest.Name, plugin.Manifest.Version)
        : "";

    public bool IsAuthenticated => Provider?.State.IsAuthenticated == true;
    public string AuthenticationStatus => _text[IsAuthenticated
        ? LanguageManager.Keys.Authenticated
        : LanguageManager.Keys.NotAuthenticated];

    public string AccountName => Provider?.State is { IsAuthenticated: true } state
        ? state.AccountName ?? state.UserId ?? SelectedService?.Name ?? ""
        : "";

    public string AccountId => Provider?.State is { IsAuthenticated: true, UserId: { } id } ? id : "";
    public Bitmap? AccountAvatar => _accountAvatar;
    public bool HasAccountAvatar => AccountAvatar is not null;
    public bool CanLogin => CanInteract && Provider is not null && !string.IsNullOrWhiteSpace(Cookie);
    public bool CanLogout => CanInteract && IsAuthenticated;
    public AsyncRelayCommand LoginCommand { get; }
    public AsyncRelayCommand LogoutCommand { get; }

    [RelayCommand]
    private void ToggleCookieEditor()
    {
        IsCookieEditorOpen = !IsCookieEditorOpen;
    }

    public Task LoginAsync()
    {
        return ExecuteAsync(async ct =>
        {
            var providerId = SelectedService?.Id;
            var provider = Provider;
            var previousState = provider?.State;
            var completed = false;
            try
            {
                if (provider is null) throw new ArgumentException(_text[LanguageManager.Keys.ProviderUnavailable]);
                if (string.IsNullOrWhiteSpace(Cookie)) throw new ArgumentException(_text[LanguageManager.Keys.CookieRequired]);
                var cookie = Cookie.Trim();
                if (!(await Task.Run(() => provider.GetAvailableMethodsAsync(ct), ct)).Contains(AuthMethod.Cookie))
                    throw new ArgumentException(_text[LanguageManager.Keys.CookieUnsupported]);
                await Task.Run(
                    () => provider.AuthenticateAsync(
                        new AuthenticationRequest(AuthMethod.Cookie,
                            new Dictionary<string, string> { ["cookie"] = cookie }), ct), ct);
                completed = true;
                SetStatus(LanguageManager.Keys.LoginCompleted);
            }
            finally
            {
                Cookie = "";
                IsCookieEditorOpen = !IsAuthenticated;
                if (providerId is not null && (completed || provider?.State != previousState))
                    Context.NotifyAuthenticationChanged(providerId);
                RaiseAccountProperties();
            }
        });
    }

    public Task LogoutAsync()
    {
        return ExecuteAsync(async ct =>
        {
            var providerId = SelectedService?.Id;
            var provider = Provider ?? throw new ArgumentException(_text[LanguageManager.Keys.ProviderUnavailable]);
            var previousState = provider.State;
            var completed = false;
            try
            {
                await Task.Run(() => provider.SignOutAsync(ct), ct);
                completed = true;
                SetStatus(LanguageManager.Keys.LogoutCompleted);
            }
            finally
            {
                Cookie = "";
                IsCookieEditorOpen = !IsAuthenticated;
                if (providerId is not null && (completed || provider.State != previousState))
                    Context.NotifyAuthenticationChanged(providerId);
                RaiseAccountProperties();
            }
        });
    }

    private void RefreshServices()
    {
        var providers = _host.Get<IProviderRegistry>().Providers.Where(p => p is IAuthenticationProvider).ToArray();
        var options = providers.Select(p => new ProviderOption(p.Id, p.Descriptor.Name)).ToArray();
        var selectedId = SelectedService?.Id
                         ?? providers.FirstOrDefault(p => p is IAuthenticationProvider { State.IsAuthenticated: true })
                             ?.Id;
        if (!Services.SequenceEqual(options))
        {
            Services.Clear();
            foreach (var option in options) Services.Add(option);
        }

        SelectedService = Services.FirstOrDefault(option => option.Id == selectedId) ?? Services.FirstOrDefault();
        OnPropertyChanged(nameof(HasServices));
        RaiseAccountProperties();
    }

    private void RaiseAccountProperties()
    {
        UpdateAvatar();
        foreach (var name in new[]
                 {
                     nameof(HasPluginAttribution), nameof(PluginAttribution), nameof(IsAuthenticated),
                     nameof(AuthenticationStatus), nameof(AccountName), nameof(AccountId), nameof(CanLogin),
                     nameof(CanLogout)
                 })
            OnPropertyChanged(name);
    }

    private void UpdateAvatar()
    {
        if (_closing) return;
        var state = Provider?.State;
        (string? ProviderId, string? UserId, Uri? Uri) identity = state is { IsAuthenticated: true }
            ? (SelectedService?.Id, state.UserId, state.AvatarUri)
            : default;
        if (identity == _avatarIdentity) return;
        _avatarIdentity = identity;
        _avatarCancellation?.Cancel();
        _avatarCancellation?.Dispose();
        _avatarCancellation = null;
        SetAvatar(null);
        if (identity.Uri is not { } uri) return;
        _avatarCancellation = CancellationTokenSource.CreateLinkedTokenSource(_avatarLifetime.Token);
        _avatarTasks.RemoveAll(task => task.IsCompleted);
        _avatarTasks.Add(LoadAvatarAsync(uri, _avatarCancellation.Token));
    }

    private async Task LoadAvatarAsync(Uri uri, CancellationToken cancellationToken)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await _artwork.LoadAsync(uri, cancellationToken);
            if (_closing || cancellationToken.IsCancellationRequested || uri != _avatarIdentity.Uri) return;
            SetAvatar(bitmap);
            bitmap = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private void SetAvatar(Bitmap? bitmap)
    {
        var previous = _accountAvatar;
        if (!SetProperty(ref _accountAvatar, bitmap, nameof(AccountAvatar))) return;
        OnPropertyChanged(nameof(HasAccountAvatar));
        previous?.Dispose();
    }

    public override Task ActivateAsync()
    {
        RefreshServices();
        return Task.CompletedTask;
    }

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        RaiseAccountProperties();
    }

    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        RefreshServices();
    }

    public override async ValueTask DisposeAsync()
    {
        _closing = true;
        await _avatarLifetime.CancelAsync();
        await base.DisposeAsync();
        await Task.WhenAll(_avatarTasks);
        SetAvatar(null);
        _avatarCancellation?.Dispose();
        _avatarLifetime.Dispose();
    }
}
