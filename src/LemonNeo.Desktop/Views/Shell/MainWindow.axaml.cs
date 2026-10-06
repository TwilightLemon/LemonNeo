using LemonNeo.Core.Lyrics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using LemonNeo.Desktop.ViewModels.Library;
using LemonNeo.Desktop.ViewModels.Shell;
using LemonNeo.Desktop.Views.Library;
using LemonNeo.Desktop.Views.Lyrics;
using LemonNeo.Desktop.Views.Shared;

namespace LemonNeo.Desktop.Views.Shell;

public partial class MainWindow : Window
{
    public static readonly StyledProperty<bool> IsDesktopLyricsOpenProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(IsDesktopLyricsOpen));
    public bool IsDesktopLyricsOpen { get => GetValue(IsDesktopLyricsOpenProperty); private set => SetValue(IsDesktopLyricsOpenProperty, value); }
    private DesktopLyricsWindow? _desktopLyrics;
    private PixelPoint? _desktopLyricsPosition;
    private bool _seeking;
    private CancellationTokenSource? _queueAnimation;
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { VolumePopup.IsOpen = false; CloseDesktopLyrics(); };
        // Avalonia draws Windows captions and preserves native macOS traffic lights.
        // Keep system decorations on Linux, where client-area extension varies by window manager.
        ExtendClientAreaToDecorationsHint = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        TopBar.Classes.Set("windows", OperatingSystem.IsWindows());
        Sidebar.Classes.Set("macos", OperatingSystem.IsMacOS());
        MacCaptionDragArea.IsVisible = OperatingSystem.IsMacOS();
        Timeline.AddHandler(PointerPressedEvent, OnSeekStarted, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.AddHandler(PointerReleasedEvent, OnSeekFinished, RoutingStrategies.Bubble, handledEventsToo: true);
        Timeline.AddHandler(Thumb.DragCompletedEvent, OnThumbDragCompleted, handledEventsToo: true);
        Timeline.AddHandler(KeyDownEvent, OnSeekKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Timeline.KeyUp += OnSeekKeyUp;
        // Command on macOS, Control elsewhere. Keep platform-native editing shortcuts intact.
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, modifier), Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => ShowCreatePlaylistAsync(importFolder: true)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Left, KeyModifiers.Alt),
            Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => (DataContext as MainViewModel)?.Navigation.GoBackAsync() ?? Task.CompletedTask) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Right, KeyModifiers.Alt),
            Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => (DataContext as MainViewModel)?.Navigation.GoForwardAsync() ?? Task.CompletedTask) });
    }

    private void OnToggleDesktopLyrics(object? sender, RoutedEventArgs args)
    {
        if (_desktopLyrics is not null) { CloseDesktopLyrics(); return; }
        if (DataContext is not MainViewModel vm) return;
        var window = new DesktopLyricsWindow { DataContext = vm.Lyrics };
        _desktopLyrics = window;
        window.Closed += (_, _) =>
        {
            _desktopLyricsPosition = window.Position;
            _desktopLyrics = null;
            IsDesktopLyricsOpen = false;
        };
        window.ShowNear(this, _desktopLyricsPosition);
        IsDesktopLyricsOpen = true;
    }

    public void CloseDesktopLyrics() => _desktopLyrics?.Close();

    private void OnToggleVolume(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel { Playback.CanAdjustVolume: true } vm) return;
        if (!VolumePopup.IsOpen) vm.CloseQueueCommand.Execute(null);
        VolumePopup.IsOpen = !VolumePopup.IsOpen;
    }

    private async void OnVolumeOpened(object? sender, EventArgs args)
    {
        VolumeSlider.Focus();
        if (DataContext is MainViewModel vm) await vm.Playback.RefreshVolumeAsync();
    }

    private void OnVolumeClosed(object? sender, EventArgs args) => VolumeButton.Focus();

    private void OnVolumeKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        VolumePopup.IsOpen = false;
        args.Handled = true;
    }

    private void OnPlaybackBarSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        var inset = args.NewSize.Height + PlaybackBar.Margin.Bottom + 12;
        Resources["Space.PlaybackInset"] = new Thickness(0, 0, 0, inset);
        Resources["Space.PlaybackFooterInset"] = new Thickness(0, 12, 0, inset);
    }

    private void OnPageContentSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        // A fixed fade distance keeps the transition consistent when the window is resized.
        var height = args.NewSize.Height;
        if (height <= 0 || PageContent.OpacityMask is not LinearGradientBrush fade) return;
        fade.StartPoint = new RelativePoint(0, Math.Max(0, height - 64), RelativeUnit.Absolute);
        fade.EndPoint = new RelativePoint(0, height, RelativeUnit.Absolute);
    }

    private async void OnQueueOpened(object? sender, EventArgs args)
    {
        VolumePopup.IsOpen = false;
        _queueAnimation?.Cancel();
        QueueCard.Focus();
        if (DataContext is not MainViewModel vm || vm.SharedContext.Appearance.Current.ReduceMotion) return;
        var cancellation = new CancellationTokenSource();
        _queueAnimation = cancellation;
        QueueCard.RenderTransform = new TranslateTransform();
        try
        {
            await new Animation
            {
                Duration = TimeSpan.FromMilliseconds(200), Easing = new CubicEaseOut(), FillMode = FillMode.None,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.XProperty, 40d), new Setter(Visual.OpacityProperty, 0d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.XProperty, 0d), new Setter(Visual.OpacityProperty, 1d) } }
                }
            }.RunAsync(QueueCard, cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_queueAnimation, cancellation))
            {
                QueueCard.RenderTransform = null; QueueCard.Opacity = 1;
                _queueAnimation = null;
            }
            cancellation.Dispose();
        }
    }
    private void OnQueueClosed(object? sender, EventArgs args)
    {
        _queueAnimation?.Cancel();
        QueueButton.Focus();
    }
    private void OnQueueKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && DataContext is MainViewModel vm)
        {
            vm.CloseQueueCommand.Execute(null);
            args.Handled = true;
        }
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || DataContext is not MainViewModel vm) return;
        args.Handled = true;
        if (vm.SearchCommand.CanExecute(null)) await vm.SearchCommand.ExecuteAsync(null);
    }

    private void OnSeekStarted(object? sender, PointerPressedEventArgs args)
    {
        if (DataContext is not MainViewModel { Playback.CanSeek: true } shell) return;
        var vm = shell.Playback;
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
        if (IsSeekKey(args.Key) && DataContext is MainViewModel { Playback.CanSeek: true } vm) vm.Playback.IsSeeking = true;
    }
    private async void OnSeekKeyUp(object? sender, KeyEventArgs args)
    {
        if (IsSeekKey(args.Key)) await CommitSeekAsync();
    }
    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    private async Task CommitSeekAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        try { await vm.Playback.SeekAsync(Timeline.Value); }
        finally { vm.Playback.EndSeek(); }
    }

    private async void OnCreatePlaylist(object? sender, RoutedEventArgs args) => await ShowCreatePlaylistAsync();

    private async Task ShowCreatePlaylistAsync(bool importFolder = false)
    {
        if (DataContext is not MainViewModel { SharedContext.IsInitialized: true } shell) return;
        var vm = shell.Library;
        var result = await new PlaylistDialog(null, importFolder).ShowDialog<PlaylistEditResult?>(this);
        if (result is not null) await vm.CreatePlaylistAsync(result.Name, result.Description, result.FolderPath);
    }

    public async Task EditPlaylistAsync()
    {
        if (DataContext is not MainViewModel { Navigation.Current: LibraryViewModel { CanEditPlaylist: true, SelectedPlaylist: { } playlist } vm }) return;
        var result = await new PlaylistDialog(playlist).ShowDialog<PlaylistEditResult?>(this);
        if (result is not null) await vm.UpdatePlaylistAsync(playlist.Id, result.Name, result.Description);
    }

    public async Task DeletePlaylistAsync()
    {
        if (DataContext is not MainViewModel { Navigation.Current: LibraryViewModel { CanEditPlaylist: true, SelectedPlaylist: { } playlist } vm }) return;
        if (await new ConfirmationDialog(vm.GetDeletePlaylistPrompt(playlist.Name)).ShowDialog<bool>(this))
            await vm.DeletePlaylistAsync(playlist.Id);
    }

    public async Task MoveTrackAsync()
    {
        if (DataContext is not MainViewModel { Navigation.Current: LibraryViewModel { CanEditTrack: true,
            SelectedTrack: { PlaylistId: { } playlistId, PlaylistItemId: { } itemId } row, SelectedPlaylist: { } playlist } vm }) return;
        var position = await new MoveTrackDialog(row.Number, playlist.Items.Count).ShowDialog<int?>(this);
        if (position is { } destination) await vm.MoveTrackAsync(playlistId, itemId, destination);
    }
}
