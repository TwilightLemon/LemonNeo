using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Library;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Tools;

public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly DesktopContext _context;
    private readonly ISettingsScope _settings;
    private readonly PlaybackSessionService _session;
    private readonly Action _notifyEditors;
    private PlaylistEntryId? _currentEntry;
    private PlaylistEntryId? _latestEntry;
    private bool _dialogOpen;
    private bool _manualTarget;
    private bool _manualScope;
    [ObservableProperty] private string _playbackProfileStatus = "";

    public string? EditImpactHint
    {
        get
        {
            var playing = _session.Snapshot;
            if (playing.AppliedProfile is not { } applied) return null;
            var origin = playing.CurrentPlaylistEntry;
            if ((ScopeIndex >= 1 && origin?.PlaylistId != _playlist?.Id) ||
                (ScopeIndex == 2 && origin?.ItemId != _item?.Id))
                return _context.Text[LanguageManager.Keys.ProfileOtherTarget];
            if ((ScopeIndex == 0 && applied.PlaylistId.HasValue) || (ScopeIndex == 1 && applied.ItemId.HasValue))
                return _context.Text[LanguageManager.Keys.ProfileMoreSpecific];
            return null;
        }
    }

    internal void UpdatePlaybackStatus(string message)
    {
        PlaybackProfileStatus = message;
        OnPropertyChanged(nameof(EditImpactHint));
    }
    private bool _reading;
    private DecodeProfile _profile = new();
    private Playlist? _playlist;
    private PlaylistItem? _item;
    private bool _initialized;
    [ObservableProperty] private IReadOnlyList<string> _scopes = [];
    [ObservableProperty] private int _scopeIndex;
    [ObservableProperty] private IReadOnlyList<Target> _playlists = [];
    [ObservableProperty] private Target? _selectedPlaylist;
    [ObservableProperty] private IReadOnlyList<Target> _songs = [];
    [ObservableProperty] private Target? _selectedSong;
    [ObservableProperty] private decimal? _speed = 1;
    [ObservableProperty] private IReadOnlyList<Preset> _presets = [];
    [ObservableProperty] private Preset? _selectedPreset;
    [ObservableProperty] private string _presetName = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private int _selectedBandIndex = 2;
    [ObservableProperty] private int _previewSampleRate = EqualizerResponse.ReferenceSampleRate;
    public DesktopNotificationService EqualizerNotifications { get; } = new();
    public IReadOnlyList<string> FrequencyLabels { get; } = DecodeProfile.BandFrequencies
        .Select(frequency => frequency >= 1000 ? $"{frequency / 1000} kHz" : $"{frequency} Hz").ToArray();

    public ImmutableArray<double> CurveGains
    {
        get => _profile.Gains;
        set { if (!value.IsDefault && !_profile.Gains.SequenceEqual(value)) Write(_profile with { Gains = value }); }
    }

    public decimal? SelectedGain
    {
        get => (uint)SelectedBandIndex < CurveGains.Length ? (decimal)CurveGains[SelectedBandIndex] : null;
        set
        {
            if (value is >= -12 and <= 12 && (uint)SelectedBandIndex < CurveGains.Length)
                CurveGains = CurveGains.SetItem(SelectedBandIndex, (double)value.Value);
        }
    }

    partial void OnSelectedBandIndexChanged(int value) => OnPropertyChanged(nameof(SelectedGain));
    public bool Quick { get; }
    public bool ShowPlaylist => !Quick && ScopeIndex >= 1;
    public bool ShowQuickTarget => Quick && ScopeIndex > 0;
    public bool ShowSong => !Quick && ScopeIndex == 2;
    public bool HasOverride => ScopeIndex switch { 1 => _playlist?.DecodeProfile != null, 2 => _item?.DecodeProfile != null, _ => false };
    public bool CanCreateOverride => !HasOverride && (ScopeIndex == 1 && _playlist is not null || ScopeIndex == 2 && _item is not null);
    public bool CanFollowPlayback => !Quick && _manualTarget && _latestEntry is not null;
    public bool SupportsLiveProfile => _session.CurrentSupportsProfiles;
    public bool CanEditProfile => (!Quick || SupportsLiveProfile) && (ScopeIndex < 1 || _playlist is not null)
        && (ScopeIndex != 2 || _item is not null);
    public string? ScopeHint => ScopeIndex switch
    {
        2 when _item is null => _context.Text[LanguageManager.Keys.ProfileEmptyPlaylist],
        2 when !HasOverride && _playlist?.DecodeProfile is not null =>
            _context.Text.Format(LanguageManager.Keys.ProfileUsingPlaylist, _playlist.Name),
        1 or 2 when !HasOverride => _context.Text[LanguageManager.Keys.ProfileUsingGlobal],
        1 => _context.Text[LanguageManager.Keys.ProfilePlaylistOverride],
        2 => _context.Text[LanguageManager.Keys.ProfileSongOverride],
        _ => null
    };
    public string? TargetName => ScopeIndex switch
    {
        1 => _playlist?.Name,
        2 => $"{_playlist?.Name} · {SelectedSong?.Name}",
        _ => null
    };
    public bool CanDelete => SelectedPreset?.Custom == true;

    public ProfileEditorViewModel(DesktopContext context, bool quick, Action notifyEditors)
    {
        _context = context;
        _settings = context.Host.Settings.ForPackage(PackageNames.Application);
        _session = context.Host.Get<PlaybackSessionService>();
        _notifyEditors = notifyEditors;
        Quick = quick;
    }

    internal void SetDialogOpen(bool open)
    {
        _dialogOpen = open;
        if (!open && _latestEntry != _currentEntry) RefreshTargets(_latestEntry);
    }

    public void RefreshTargets(PlaylistEntryId? current, bool followNow = false)
    {
        _latestEntry = current;
        // An open EQ editor keeps the song the user chose, even after the queue advances.
        if (_dialogOpen && !followNow) current = _currentEntry;
        var changed = !_initialized || current != _currentEntry;
        _currentEntry = current;
        _reading = true;
        try
        {
            var follow = Quick || (!_manualTarget && current is not null);
            var listId = follow ? current?.PlaylistId : SelectedPlaylist?.Id;
            var songId = follow ? current?.ItemId : SelectedSong?.Id;
            Playlists = _context.Playlists.Select(p => new Target(p.Id, p.Name)).ToArray();
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == listId) ?? (!Quick && listId is null ? Playlists.FirstOrDefault() : null);
            LoadSongs(songId);
            if (followNow || !_initialized || (follow && changed && (Quick || !_manualScope)))
                ScopeIndex = _item?.DecodeProfile != null ? 2 : _playlist?.DecodeProfile != null ? 1 : 0;
            _initialized = true;
        }
        finally { _reading = false; }
        RefreshValues();
    }

    private void LoadSongs(Guid? songId)
    {
        _playlist = _context.Playlists.FirstOrDefault(p => p.Id == SelectedPlaylist?.Id);
        Songs = _playlist?.Items.OrderBy(i => FractionalPlaylistOrderingService.Parse(i.SortKey)).ThenBy(i => i.Id)
            .Select((item, index) => new Target(item.Id, $"{index + 1}. {item.Track.Title}")).ToArray() ?? [];
        SelectedSong = Songs.FirstOrDefault(i => i.Id == songId) ?? (!Quick && songId is null ? Songs.FirstOrDefault() : null);
        _item = _playlist?.Items.FirstOrDefault(i => i.Id == SelectedSong?.Id);
        var index = ScopeIndex;
        var hasSongs = Quick ? _item is not null : _context.Playlists.Any(list => list.Items.Count > 0);
        var hasPlaylists = Quick ? _playlist is not null : Playlists.Count > 0;
        var scopeCount = hasSongs ? 3 : hasPlaylists ? 2 : 1;
        Scopes = new[] { _context.Text[LanguageManager.Keys.ProfileGlobal], _context.Text[LanguageManager.Keys.ProfilePlaylist],
            _context.Text[LanguageManager.Keys.ProfileSong] }.Take(scopeCount).ToArray();
        ScopeIndex = Math.Clamp(index, 0, Scopes.Count - 1);
    }

    partial void OnSelectedPlaylistChanged(Target? value)
    {
        if (_reading || value is null) return;
        _manualTarget = true;
        _reading = true;
        try { LoadSongs(null); }
        finally { _reading = false; }
        RefreshValues();
    }

    partial void OnSelectedSongChanged(Target? value)
    {
        if (_reading || value is null) return;
        _manualTarget = true;
        _item = _playlist?.Items.FirstOrDefault(i => i.Id == value.Id);
        RefreshValues();
    }

    partial void OnScopeIndexChanged(int value)
    {
        if (_reading || value < 0 || !_initialized) return;
        _manualScope = true;
        RefreshValues();
    }

    internal void RefreshValues()
    {
        if (!_initialized) return;
        _reading = true;
        try
        {
            var prefs = _settings.Get<DecodePreferences>();
            _profile = ((ScopeIndex == 2 ? _item?.DecodeProfile : null) ??
                (ScopeIndex >= 1 ? _playlist?.DecodeProfile : null) ?? prefs.Global).Normalize();
            Speed = (decimal)_profile.Speed;
            OnPropertyChanged(nameof(CurveGains));
            OnPropertyChanged(nameof(SelectedGain));
            var presets = new List<Preset>
            {
                new(_context.Text[LanguageManager.Keys.EqFlat], DecodeProfile.Flat),
                new(_context.Text[LanguageManager.Keys.EqBass], [5, 2, 0, 0, 0]),
                new(_context.Text[LanguageManager.Keys.EqVocal], [-2, -1, 2, 3, 0]),
                new(_context.Text[LanguageManager.Keys.EqBright], [0, 0, 0, 2, 4])
            };
            presets.AddRange(prefs.Presets.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name) &&
                !p.Gains.IsDefault && p.Gains.Length == DecodeProfile.BandFrequencies.Length).Select(p => new Preset(p.Name, p.Gains, true)));
            var name = SelectedPreset?.Name;
            Presets = presets;
            SelectedPreset = presets.FirstOrDefault(p => p.Name == name && p.Gains.SequenceEqual(_profile.Gains))
                ?? presets.FirstOrDefault(p => p.Gains.SequenceEqual(_profile.Gains));
            foreach (var property in new[] { nameof(ShowPlaylist), nameof(ShowSong), nameof(ShowQuickTarget),
                         nameof(ScopeHint), nameof(EditImpactHint), nameof(HasOverride), nameof(CanCreateOverride), nameof(CanFollowPlayback), nameof(TargetName),
                         nameof(SupportsLiveProfile), nameof(CanEditProfile), nameof(CanDelete) }) OnPropertyChanged(property);
        }
        finally { _reading = false; }
    }

    partial void OnSpeedChanged(decimal? value)
    {
        if (value is >= 0.5m and <= 2m) Write(_profile with { Speed = (double)value });
    }

    partial void OnSelectedPresetChanged(Preset? value)
    {
        if (value != null) Write(_profile with { Gains = value.Gains });
        OnPropertyChanged(nameof(CanDelete));
    }

    [RelayCommand]
    private void FollowPlayback()
    {
        _manualTarget = false;
        _manualScope = false;
        RefreshTargets(_latestEntry, followNow: true);
    }

    [RelayCommand]
    private void CreateOverride()
    {
        if (CanCreateOverride) Write(_profile);
    }

    [RelayCommand]
    private void Inherit() => Write(null);

    private async void Write(DecodeProfile? profile)
    {
        if (_reading || !_initialized || ScopeIndex < 0) return;
        var playlist = ScopeIndex >= 1 ? _playlist?.Id : null;
        var song = ScopeIndex == 2 ? _item?.Id : null;
        if (ScopeIndex >= 1 && playlist is null || ScopeIndex == 2 && song is null) return;
        try
        {
            Error = null;
            var save = _session.SetProfileAsync(playlist, song, profile);
            _notifyEditors();
            await save;
        }
        catch (Exception ex)
        {
            NotifyFailure(ex);
            _notifyEditors();
        }
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0 || Presets.Any(p => string.Equals(name, p.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Error = _context.Text[LanguageManager.Keys.EqNameRequired];
            return;
        }
        try
        {
            _settings.Apply<DecodePreferences>(prefs => prefs with { Presets = prefs.Presets.Add(new(name, _profile.Gains)) });
        }
        catch (Exception exception)
        {
            NotifyFailure(exception);
            return;
        }
        PresetName = "";
        Error = null;
        _notifyEditors();
        _reading = true;
        try { SelectedPreset = Presets.First(p => p.Name == name); }
        finally { _reading = false; }
        EqualizerNotifications.Show(_context.Text.Format(LanguageManager.Keys.EqPresetSaved, name),
            NotificationLevel.Success);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is not { Custom: true } preset) return;
        try
        {
            _settings.Apply<DecodePreferences>(prefs => prefs with { Presets = prefs.Presets.RemoveAll(p => p?.Name == preset.Name) });
        }
        catch (Exception exception)
        {
            NotifyFailure(exception);
            return;
        }
        Error = null;
        _notifyEditors();
        EqualizerNotifications.Show(_context.Text.Format(LanguageManager.Keys.EqPresetDeleted, preset.Name),
            NotificationLevel.Success);
    }

    private void NotifyFailure(Exception exception)
    {
        var notifications = _dialogOpen ? EqualizerNotifications : _context.Notifications;
        notifications.Show(_context.Text.Format(LanguageManager.Keys.OperationFailed, exception.Message),
            NotificationLevel.Error);
    }

    public sealed record Target(Guid Id, string Name) { public override string ToString() => Name; }
    public sealed record Preset(string Name, ImmutableArray<double> Gains, bool Custom = false)
    {
        public override string ToString() => Name;
    }
}
