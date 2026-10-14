using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Controls.Shared;
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

    private DesktopLyricsWindow? _desktopLyrics;
    private bool _preserveDesktopLyricsPreference;
    private CancellationTokenSource? _queueAnimation;
    private bool _seeking;

    public MainWindow()
    {
        InitializeComponent();
        _ = new ListReorderBehavior(PlaylistList, () => DataContext is MainViewModel { CanInteract: true },
            (source, target, after) => DataContext is MainViewModel vm &&
                                       source is PlaylistMenuItemViewModel playlist &&
                                       target is PlaylistMenuItemViewModel destination
                ? vm.ReorderPlaylistsAsync(playlist, destination, after)
                : Task.CompletedTask);
        Closed += (_, _) =>
        {
            VolumePopup.IsOpen = false;
            SourceDetailsPopup.IsOpen = false;
            CloseDesktopLyrics();
        };
        // Avalonia draws Windows captions and preserves native macOS traffic lights.
        // Keep system decorations on Linux, where client-area extension varies by window manager.
        ExtendClientAreaToDecorationsHint = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        TopBar.Classes.Set("windows", OperatingSystem.IsWindows());
        Sidebar.Classes.Set("macos", OperatingSystem.IsMacOS());
        MacCaptionDragArea.IsVisible = OperatingSystem.IsMacOS();
        Timeline.AddHandler(PointerPressedEvent, OnSeekStarted, RoutingStrategies.Tunnel, true);
        Timeline.AddHandler(PointerReleasedEvent, OnSeekFinished, RoutingStrategies.Bubble, true);
        Timeline.AddHandler(Thumb.DragCompletedEvent, OnThumbDragCompleted, handledEventsToo: true);
        Timeline.AddHandler(KeyDownEvent, OnSeekKeyDown, RoutingStrategies.Tunnel, true);
        Timeline.KeyUp += OnSeekKeyUp;
        // Command on macOS, Control elsewhere. Keep platform-native editing shortcuts intact.
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.O, modifier),
            Command = new AsyncRelayCommand(() => ShowCreatePlaylistAsync(true))
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Left, KeyModifiers.Alt),
            Command = new AsyncRelayCommand(() =>
                (DataContext as MainViewModel)?.Navigation.GoBackAsync() ?? Task.CompletedTask)
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Right, KeyModifiers.Alt),
            Command = new AsyncRelayCommand(() =>
                (DataContext as MainViewModel)?.Navigation.GoForwardAsync() ?? Task.CompletedTask)
        });
    }

    public bool IsDesktopLyricsOpen
    {
        get => GetValue(IsDesktopLyricsOpenProperty);
        private set => SetValue(IsDesktopLyricsOpenProperty, value);
    }

    private void OnToggleDesktopLyrics(object? sender, RoutedEventArgs args)
    {
        if (_desktopLyrics is not null)
        {
            CloseDesktopLyrics(false);
            return;
        }

        OpenDesktopLyrics(true);
    }

    public void RestoreDesktopLyrics()
    {
        if (DataContext is MainViewModel vm && vm.SharedContext.Settings.Get<DesktopLyricsPreferences>().IsEnabled)
            OpenDesktopLyrics(false);
    }

    private void OpenDesktopLyrics(bool updatePreference)
    {
        if (_desktopLyrics is not null || DataContext is not MainViewModel vm) return;
        _preserveDesktopLyricsPreference = false;
        var window = new DesktopLyricsWindow { DataContext = vm.Lyrics };
        _desktopLyrics = window;
        window.Closed += (_, _) =>
        {
            _desktopLyrics = null;
            IsDesktopLyricsOpen = false;
            if (!_preserveDesktopLyricsPreference)
                vm.SharedContext.Settings.Apply<DesktopLyricsPreferences>(value => value with { IsEnabled = false });
            _preserveDesktopLyricsPreference = false;
        };
        window.ShowNear(this);
        IsDesktopLyricsOpen = true;
        if (updatePreference)
            vm.SharedContext.Settings.Apply<DesktopLyricsPreferences>(value => value with { IsEnabled = true });
    }

    public void CloseDesktopLyrics(bool preservePreference = true)
    {
        if (_desktopLyrics is null) return;
        _preserveDesktopLyricsPreference = preservePreference;
        _desktopLyrics.Close();
    }

    public void PrepareForBackground()
    {
        VolumePopup.IsOpen = false;
        SourceDetailsPopup.IsOpen = false;
        if (DataContext is MainViewModel vm) vm.CloseQueueCommand.Execute(null);
    }

    private void OnToggleVolume(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel { Playback.CanAdjustVolume: true } vm) return;
        SourceDetailsPopup.IsOpen = false;
        if (!VolumePopup.IsOpen) vm.CloseQueueCommand.Execute(null);
        VolumePopup.IsOpen = !VolumePopup.IsOpen;
    }

    private async void OnVolumeOpened(object? sender, EventArgs args)
    {
        VolumeSlider.Focus();
        if (DataContext is MainViewModel vm) await vm.Playback.RefreshVolumeAsync();
    }

    private void OnVolumeClosed(object? sender, EventArgs args)
    {
        VolumeButton.Focus();
    }

    private void OnVolumeKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        VolumePopup.IsOpen = false;
        args.Handled = true;
    }

    private void OnToggleSourceDetails(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel { Playback.HasSourceDetails: true } vm) return;
        if (!vm.Playback.IsSourceDetailsOpen)
        {
            VolumePopup.IsOpen = false;
            vm.CloseQueueCommand.Execute(null);
        }

        vm.Playback.IsSourceDetailsOpen = !vm.Playback.IsSourceDetailsOpen;
    }

    private void OnSourceDetailsOpened(object? sender, EventArgs args)
    {
        SourceDetailsCard.Focus();
    }

    private void OnSourceDetailsClosed(object? sender, EventArgs args)
    {
        CurrentAudioQuality.Focus();
    }

    private void OnSourceDetailsKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        SourceDetailsPopup.IsOpen = false;
        args.Handled = true;
    }

    private void OnPlaybackBarSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        var inset = args.NewSize.Height + PlaybackBar.Margin.Bottom + 12;
        Resources["Space.PlaybackInset"] = new Thickness(0, 0, 0, inset);
        Resources["Space.PlaybackFooterInset"] = new Thickness(0, 12, 0, inset);
        Resources["Space.PlaybackFloatingInset"] = new Thickness(0, 0, 16, inset);
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
        SourceDetailsPopup.IsOpen = false;
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
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters = { new Setter(TranslateTransform.XProperty, 40d), new Setter(OpacityProperty, 0d) }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters = { new Setter(TranslateTransform.XProperty, 0d), new Setter(OpacityProperty, 1d) }
                    }
                }
            }.RunAsync(QueueCard, cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_queueAnimation, cancellation))
            {
                QueueCard.RenderTransform = null;
                QueueCard.Opacity = 1;
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
        if (IsSeekKey(args.Key) && DataContext is MainViewModel { Playback.CanSeek: true } vm)
            vm.Playback.IsSeeking = true;
    }

    private async void OnSeekKeyUp(object? sender, KeyEventArgs args)
    {
        if (IsSeekKey(args.Key)) await CommitSeekAsync();
    }

    private static bool IsSeekKey(Key key)
    {
        return key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    }

    private async Task CommitSeekAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            await vm.Playback.SeekAsync(Timeline.Value);
        }
        finally
        {
            vm.Playback.EndSeek();
        }
    }

    private async void OnCreatePlaylist(object? sender, RoutedEventArgs args)
    {
        await ShowCreatePlaylistAsync();
    }

    private async Task ShowCreatePlaylistAsync(bool importFolder = false)
    {
        if (DataContext is not MainViewModel { SharedContext.IsInitialized: true } shell) return;
        var vm = shell.Library;
        var result = await new PlaylistDialog(null, importFolder).ShowDialog<PlaylistEditResult?>(this);
        if (result is not null) await vm.CreatePlaylistAsync(result.Name, result.Description, result.FolderPath);
    }

    public async Task EditPlaylistAsync()
    {
        if (DataContext is not MainViewModel
            {
                Navigation.Current: LibraryViewModel { CanEditPlaylist: true, SelectedPlaylist: { } playlist } vm
            }) return;
        var result = await new PlaylistDialog(playlist).ShowDialog<PlaylistEditResult?>(this);
        if (result is not null) await vm.UpdatePlaylistAsync(playlist.Id, result.Name, result.Description);
    }

    public async Task DeletePlaylistAsync()
    {
        if (DataContext is not MainViewModel
            {
                Navigation.Current: LibraryViewModel { CanEditPlaylist: true, SelectedPlaylist: { } playlist } vm
            }) return;
        if (await new ConfirmationDialog(vm.GetDeletePlaylistPrompt(playlist.Name)).ShowDialog<bool>(this))
            await vm.DeletePlaylistAsync(playlist.Id);
    }

    public async Task MoveTrackAsync()
    {
        if (DataContext is not MainViewModel
            {
                Navigation.Current: LibraryViewModel
                {
                    CanEditTrack: true,
                    SelectedTrack: { PlaylistId: { } playlistId, PlaylistItemId: { } itemId } row,
                    SelectedPlaylist: { } playlist
                } vm
            }) return;
        var position = await new MoveTrackDialog(row.Number, playlist.Items.Count).ShowDialog<int?>(this);
        if (position is { } destination) await vm.MoveTrackAsync(playlistId, itemId, destination);
    }
}