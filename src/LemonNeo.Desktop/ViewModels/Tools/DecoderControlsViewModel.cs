using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Application.Playback;
using LemonNeo.Desktop.Services;
using LemonNeo.Core.Playback;

namespace LemonNeo.Desktop.ViewModels.Tools;

public sealed partial class DecoderControlsViewModel : ObservableObject, IDisposable
{
    private readonly DesktopContext _context;
    private readonly PlaybackSessionService _session;
    private readonly ISettingsScope _settings;
    private Dictionary<string, AudioOutputViewModel> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _preferences;
    private Guid? _queueItemId;
    private bool? _profileSupport;
    [ObservableProperty] private AudioOutputViewModel? _quickOutput;
    [ObservableProperty] private IReadOnlyList<AudioOutputViewModel> _outputsList = [];
    [ObservableProperty] private string _decoderToolTip = "";
    [ObservableProperty] private bool _usingScopedProfile;
    public ProfileEditorViewModel QuickProfile { get; }
    public ProfileEditorViewModel EditorProfile { get; }

    public DecoderControlsViewModel(DesktopContext context)
    {
        _context = context;
        _session = context.Host.Get<PlaybackSessionService>();
        _settings = context.Host.Settings.ForPackage(PackageNames.Application);
        QuickProfile = new(context, true, RefreshProfiles);
        EditorProfile = new(context, false, RefreshProfiles);
    }

    public void Initialize()
    {
        _preferences = _settings.Subscribe<PlaybackPreferences>(_ => Dispatcher.UIThread.Post(RefreshCurrent));
        _session.ProfileChanged += OnPlaybackChanged;
        _session.MetadataChanged += OnPlaybackChanged;
        _session.StateChanged += OnPlaybackChanged;
        _context.LibraryChanged += OnLibraryChanged;
        _context.Text.Changed += OnLanguageChanged;
        ReloadOutputs();
    }

    private void OnLanguageChanged(object? sender, EventArgs args) => ReloadOutputs();

    private void ReloadOutputs()
    {
        var players = _context.Host.Get<IPlayerRegistry>().Players;
        _outputs = players.Where(p => p is IAudioOutputConfigurable { OutputBackends.Count: > 0 })
            .ToDictionary(p => p.Id, p => new AudioOutputViewModel(p.Descriptor.Name,
                (IAudioOutputConfigurable)p, _context), StringComparer.OrdinalIgnoreCase);
        OutputsList = _outputs.Values.ToArray();
        RefreshCurrent(true);
    }

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        try { _session.RefreshActiveProfile(); }
        catch (Exception ex)
        {
            _context.Notifications.Show(_context.Text.Format(LanguageManager.Keys.OperationFailed, ex.Message),
                NotificationLevel.Error);
        }
        RefreshCurrent(true);
    }
    private void OnPlaybackChanged(object? sender, PlaybackSessionChangedEventArgs args) =>
        Dispatcher.UIThread.Post(RefreshCurrent);

    private void RefreshCurrent() => RefreshCurrent(false);

    private void RefreshCurrent(bool refreshTargets)
    {
        if (_preferences is null) return;
        var snapshot = _session.Snapshot;
        var rate = snapshot.AudioInfo?.SampleRateHz;
        QuickProfile.PreviewSampleRate = EditorProfile.PreviewSampleRate = rate is > 0 ? rate.Value : EqualizerResponse.ReferenceSampleRate;
        var preferred = _settings.Get<PlaybackPreferences>().PreferredPlayerId;
        var registry = _context.Host.Get<IPlayerRegistry>();
        var playerId = snapshot.PlayerId ?? (preferred is null ? null : registry.Get(preferred)?.Id)
            ?? registry.Players.FirstOrDefault()?.Id;
        QuickOutput = playerId is null ? null : _outputs.GetValueOrDefault(playerId);
        var supports = _session.CurrentSupportsProfiles;
        if (!refreshTargets && _queueItemId == snapshot.CurrentQueueItemId && _profileSupport == supports)
        {
            UpdateStatus(snapshot);
            return;
        }
        _queueItemId = snapshot.CurrentQueueItemId;
        _profileSupport = supports;
        QuickProfile.RefreshTargets(snapshot.CurrentPlaylistEntry);
        EditorProfile.RefreshTargets(snapshot.CurrentPlaylistEntry);
        UpdateStatus(snapshot);
    }

    private void RefreshProfiles()
    {
        QuickProfile.RefreshValues();
        EditorProfile.RefreshValues();
        UpdateStatus(_session.Snapshot);
    }

    private void UpdateStatus(PlaybackSessionSnapshot snapshot)
    {
        var text = _context.Text;
        string status;
        if (snapshot.State == PlaybackState.Loading) status = text[LanguageManager.Keys.ProfilePending];
        else if (snapshot.State is not (PlaybackState.Playing or PlaybackState.Paused))
            status = text[LanguageManager.Keys.ProfileIdle];
        else if (snapshot.AppliedProfile is not { } applied)
            status = text[LanguageManager.Keys.EffectsUnavailable];
        else
        {
            var list = _context.Playlists.FirstOrDefault(p => p.Id == applied.PlaylistId);
            var entryName = QuickProfile.SelectedPlaylist?.Id == applied.PlaylistId && applied.ItemId.HasValue
                ? QuickProfile.Songs.FirstOrDefault(s => s.Id == applied.ItemId)?.Name : null;
            var source = applied.ItemId is not null
                ? text.Format(LanguageManager.Keys.ProfileActiveSong, list?.Name, entryName ?? snapshot.CurrentTrack?.Title)
                : applied.PlaylistId is not null
                    ? text.Format(LanguageManager.Keys.ProfileActivePlaylist, list?.Name)
                    : text[snapshot.CurrentPlaylistEntry is null ? LanguageManager.Keys.ProfileNoOrigin : LanguageManager.Keys.ProfileGlobal];
            var gains = applied.Profile.Gains;
            var eq = QuickProfile.Presets.FirstOrDefault(p => p.Gains.SequenceEqual(gains))?.Name
                ?? string.Join(" / ", gains.Select(g => g.ToString("+0.##;-0.##;0", text.Culture))) + " dB";
            status = text.Format(LanguageManager.Keys.ProfileActive, source, applied.Profile.Speed, eq);
        }
        QuickProfile.UpdatePlaybackStatus(status);
        EditorProfile.UpdatePlaybackStatus(status);
        DecoderToolTip = $"{text[LanguageManager.Keys.DecoderControls]}{Environment.NewLine}{status}";
        UsingScopedProfile = snapshot.AppliedProfile?.PlaylistId is not null;
    }

    public void Dispose()
    {
        _session.ProfileChanged -= OnPlaybackChanged;
        _session.MetadataChanged -= OnPlaybackChanged;
        _session.StateChanged -= OnPlaybackChanged;
        _context.LibraryChanged -= OnLibraryChanged;
        _context.Text.Changed -= OnLanguageChanged;
        _preferences?.Dispose();
        _preferences = null;
    }
}
