using LemonNeo.Abstractions.Settings;
using LemonNeo.Application.Library;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop;

/// <summary>Shared services and persisted data, without page-specific interaction state.</summary>
public sealed class DesktopContext(DesktopHost host, LocalizationService text, AppearanceService appearance) : ObservableObject
{
    private bool _initialized;
    public DesktopHost Host { get; } = host;
    public LocalizationService Text { get; } = text;
    public ISettingsScope Settings { get; } = host.Settings.ForPackage(PackageNames.Desktop);
    public AppearanceService Appearance { get; } = appearance;
    public ObservableCollection<Playlist> Playlists { get; } = [];
    public bool IsInitialized { get => _initialized; internal set => SetProperty(ref _initialized, value); }
    public event EventHandler? LibraryChanged;
    public event Action<string>? AuthenticationChanged;
    public void NotifyAuthenticationChanged(string providerId) => AuthenticationChanged?.Invoke(providerId);
    public string LibrarySummary => Text.Format("LibraryStatistics", Playlists.Count,
        Host.Get<LibraryService>().Current.Playlists.SelectMany(p => p.Items).DistinctBy(i => i.Track.Id).Count(),
        Host.Get<LibraryService>().Current.Playlists.Sum(p => p.Items.Count));

    public void RefreshLibrary()
    {
        var values = Host.Get<LibraryService>().Current.Playlists.OrderBy(p => p.Name, StringComparer.Create(Text.Culture, true)).ToArray();
        // Preserve the sidebar selection while refreshing existing playlist objects.
        for (var i = Playlists.Count - 1; i >= 0; i--)
            if (!values.Any(p => p.Id == Playlists[i].Id)) Playlists.RemoveAt(i);
        for (var i = 0; i < values.Length; i++)
        {
            var existing = Playlists.FirstOrDefault(p => p.Id == values[i].Id);
            if (existing is null) Playlists.Insert(i, values[i]);
            else
            {
                var index = Playlists.IndexOf(existing);
                if (index != i) Playlists.Move(index, i);
                if (!ReferenceEquals(existing, values[i])) Playlists[i] = values[i];
            }
        }
        OnPropertyChanged(nameof(LibrarySummary));
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }
}
