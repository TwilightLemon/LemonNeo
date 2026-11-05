using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Shared;

/// <summary>One update check and notification shared by the shell and Settings, independent of navigation.</summary>
public sealed class UpdateViewModel : ObservableObject, IAsyncDisposable
{
    private readonly GitHubReleaseClient _client;
    private readonly ISettingsScope _settings;
    private readonly LocalizationService _text;
    private readonly IDisposable _settingsSubscription;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _checkTask = Task.CompletedTask;
    private DesktopRelease? _release;
    private string? _dismissedVersion;
    private LinguaKey _statusKey = LanguageManager.Keys.UpdateNotChecked;
    private bool _isChecking;
    private bool _closing;
    private bool _isUpdateAvailable;

    public UpdateViewModel(GitHubReleaseClient client, ISettingsScope settings, LocalizationService text)
    {
        _client = client;
        _settings = settings;
        _text = text;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !_isChecking && !_closing);
        OpenReleaseCommand = new RelayCommand(OpenRelease);
        DismissCommand = new RelayCommand(() =>
        {
            _dismissedVersion = _release?.Version;
            OnPropertyChanged(nameof(ShowNotification));
        });
        _settingsSubscription = settings.Subscribe<UpdatePreferences>(_ => OnPropertyChanged(nameof(CheckOnStartup)));
        text.Changed += OnLanguageChanged;
    }

    public string CurrentVersion => GitHubReleaseClient.CurrentVersion;
    public bool IsChecking => _isChecking;
    public bool IsUpdateAvailable => _isUpdateAvailable;
    public bool ShowNotification => IsUpdateAvailable && _release?.Version != _dismissedVersion;
    public string Status => _text[_statusKey];
    public string AvailableVersionText => _text.Format(LanguageManager.Keys.UpdateAvailable, _release?.Version ?? "");
    public string CurrentVersionText => _text.Format(LanguageManager.Keys.UpdateCurrentVersion, CurrentVersion);
    public RelayCommand OpenReleaseCommand { get; }
    public RelayCommand DismissCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }

    public bool CheckOnStartup
    {
        get => _settings.Get<UpdatePreferences>().CheckOnStartup;
        set
        {
            if (!_closing) _settings.Apply<UpdatePreferences>(current => current with { CheckOnStartup = value });
        }
    }

    public void CheckAtStartup()
    {
        if (CheckOnStartup) _ = CheckAsync();
    }

    public Task CheckAsync()
    {
        if (_closing || !_checkTask.IsCompleted) return _checkTask;
        return _checkTask = CheckCoreAsync();
    }

    private async Task CheckCoreAsync()
    {
        _isChecking = true;
        _statusKey = LanguageManager.Keys.UpdateChecking;
        RefreshProperties();
        try
        {
            var release = await _client.GetLatestAsync(_lifetime.Token);
            _isUpdateAvailable = release is not null && GitHubReleaseClient.IsNewerVersion(release.Version, CurrentVersion);
            _release = release;
            _statusKey = release is null ? LanguageManager.Keys.UpdateNoReleases
                : _isUpdateAvailable ? LanguageManager.Keys.UpdateAvailableStatus
                : LanguageManager.Keys.UpdateUpToDate;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Background failures stay in Settings and never interrupt playback or startup.
            Trace.WriteLine($"GitHub release check failed: {exception}");
            _statusKey = LanguageManager.Keys.UpdateCheckFailed;
        }
        finally
        {
            _isChecking = false;
            if (!_closing) RefreshProperties();
        }
    }

    private void OpenRelease()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_release?.ReleaseUri.AbsoluteUri ?? GitHubReleaseClient.ReleasesAddress)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Could not open GitHub Releases: {exception}");
            _statusKey = LanguageManager.Keys.UpdateOpenFailed;
            OnPropertyChanged(nameof(Status));
        }
    }

    private void RefreshProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(IsChecking), nameof(IsUpdateAvailable), nameof(ShowNotification),
                     nameof(Status), nameof(AvailableVersionText), nameof(CurrentVersionText)
                 }) OnPropertyChanged(name);
        CheckCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs args) => RefreshProperties();

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        _settingsSubscription.Dispose();
        _text.Changed -= OnLanguageChanged;
        await _lifetime.CancelAsync();
        await _checkTask;
        _lifetime.Dispose();
    }
}
