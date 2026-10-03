using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;

namespace LemonNeo.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _seeking;
    public MainWindow()
    {
        InitializeComponent();
        Timeline.AddHandler(PointerPressedEvent, OnSeekStarted, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.AddHandler(PointerReleasedEvent, OnSeekFinished, RoutingStrategies.Bubble, handledEventsToo: true);
        Timeline.AddHandler(Thumb.DragCompletedEvent, OnThumbDragCompleted, handledEventsToo: true);
        Timeline.AddHandler(KeyDownEvent, OnSeekKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.KeyUp += OnSeekKeyUp;
        // Command on macOS, Control elsewhere. Keep platform-native editing shortcuts intact.
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, modifier), Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => ShowCreatePlaylistAsync(importFolder: true)) });
    }

    private void OnSeekStarted(object? sender, PointerPressedEventArgs args)
    {
        if (DataContext is not MainViewModel { CanSeek: true } vm) return;
        _seeking = true;
        vm.IsSeeking = true;
    }
    private async void OnSeekFinished(object? sender, PointerReleasedEventArgs args)
    {
        if (!_seeking) return;
        _seeking = false;
        await CommitSeekAsync();
    }
    private async void OnThumbDragCompleted(object? sender, VectorEventArgs args)
    {
        _seeking = false;
        await CommitSeekAsync();
    }
    private void OnSeekKeyDown(object? sender, KeyEventArgs args)
    {
        if (IsSeekKey(args.Key) && DataContext is MainViewModel { CanSeek: true } vm) vm.IsSeeking = true;
    }
    private async void OnSeekKeyUp(object? sender, KeyEventArgs args)
    {
        if (IsSeekKey(args.Key)) await CommitSeekAsync();
    }
    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private async Task CommitSeekAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        try { await vm.SeekAsync(Timeline.Value); }
        finally { vm.EndSeek(); }
    }

    private async void OnCreatePlaylist(object? sender, RoutedEventArgs args) => await ShowCreatePlaylistAsync();

    private async Task ShowCreatePlaylistAsync(bool importFolder = false)
    {
        if (DataContext is not MainViewModel { CanInteract: true } vm) return;
        var result = await new PlaylistDialog(null, importFolder).ShowDialog<PlaylistEditResult?>(this);
        if (result is not null) await vm.CreatePlaylistAsync(result.Name, result.Description, result.FolderPath);
    }

    public async Task EditPlaylistAsync()
    {
        if (DataContext is not MainViewModel { CanEditPlaylist: true, SelectedPlaylist: { } playlist } vm) return;
        var result = await new PlaylistDialog(playlist).ShowDialog<PlaylistEditResult?>(this);
        if (result is not null) await vm.UpdatePlaylistAsync(playlist.Id, result.Name, result.Description);
    }

    public async Task DeletePlaylistAsync()
    {
        if (DataContext is not MainViewModel { CanEditPlaylist: true, SelectedPlaylist: { } playlist } vm) return;
        if (await new ConfirmationDialog(vm.GetDeletePlaylistPrompt(playlist.Name)).ShowDialog<bool>(this))
            await vm.DeletePlaylistAsync(playlist.Id);
    }

    public async Task MoveTrackAsync()
    {
        if (DataContext is not MainViewModel { CanEditTrack: true, SelectedTrack: { PlaylistId: { } playlistId, PlaylistItemId: { } itemId } row,
            SelectedPlaylist: { } playlist } vm) return;
        var position = await new MoveTrackDialog(row.Number, playlist.Items.Count).ShowDialog<int?>(this);
        if (position is { } destination) await vm.MoveTrackAsync(playlistId, itemId, destination);
    }
}
