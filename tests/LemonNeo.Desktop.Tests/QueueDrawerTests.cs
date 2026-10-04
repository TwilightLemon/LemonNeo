using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Configuration;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class QueueDrawerTests
{
    [AvaloniaFact]
    public async Task DrawerLoadsLazilyFiltersPlaysDuplicateItemsAndDismissesWithoutChangingPageHistory()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths,
            new(Path.Combine(AppContext.BaseDirectory, "plugins"), paths.MusicLibraryDirectory), [new DesktopTests.TestPlayerProvider()]);
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await vm.InitializeAsync();
        await appearance.SetAsync(new(ThemePreference.Light, LanguagePreference.English, true));
        var folder = Path.Combine(paths.DataDirectory, "Music");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Morning.wav"), [0]);
        File.WriteAllBytes(Path.Combine(folder, "Night.wav"), [0]);
        await vm.Library.CreatePlaylistAsync("Music", folderPath: folder);
        var sourceTracks = vm.Library.Tracks.Select(r => r.Track).ToArray();
        var first = new PlaybackQueueItem { Track = sourceTracks[0] };
        var second = new PlaybackQueueItem { Track = sourceTracks[1] };
        var duplicate = new PlaybackQueueItem { Track = first.Track };
        var request = new PlaybackRequest(RequiredSourceProviderId: "local");
        var session = runtime.Get<PlaybackSessionService>();
        session.Queue.Restore(new() { Items = [first, second, duplicate], CurrentItemId = first.Id, Request = request });
        await session.PlayQueueItemAsync(first.Id);
        vm.Playback.UpdateSnapshot();
        var window = new MainWindow { DataContext = vm, Width = 880, Height = 620 };
        window.Show();
        try
        {
            Layout();
            Assert.Null(vm.QueueDrawer);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("nav") && b.CommandParameter is "Queue" or "Lyrics");
            var currentPage = vm.Navigation.Current;
            var historyDepth = vm.Navigation.Host.StackDepth;
            var loadedPages = vm.Navigation.LoadedPageCount;
            var popup = window.FindControl<Popup>("QueuePopup")!;
            var queueButton = window.FindControl<Button>("QueueButton")!;
            Click(queueButton);
            var drawer = Assert.IsType<PlaybackQueueViewModel>(vm.QueueDrawer);
            var card = window.FindControl<Border>("QueueCard")!;
            var view = Assert.Single(card.GetVisualDescendants().OfType<QueueDrawerView>());
            Assert.True(popup.IsOpen); Assert.True(vm.IsQueueOpen);
            Assert.True(popup.IsUsingOverlayLayer);
            Assert.Same(currentPage, vm.Navigation.Current);
            Assert.Equal(historyDepth, vm.Navigation.Host.StackDepth);
            Assert.Equal(loadedPages, vm.Navigation.LoadedPageCount);
            Assert.Equal(3, drawer.Tracks.Count);
            Assert.Equal(first.Id, Assert.Single(drawer.Tracks, r => r.IsCurrent).Id);
            Assert.Equal(window.Bounds.Height, card.Bounds.Height);
            Assert.InRange(Math.Abs(card.TranslatePoint(default, window)!.Value.X + card.Bounds.Width - window.Bounds.Width), 0, 1);

            vm.Library.Filter = "Library filter";
            view.FindControl<TextBox>("QueueFilter")!.Text = first.Track.Title;
            Layout();
            Assert.Equal(2, drawer.Tracks.Count);
            Assert.Equal("Library filter", vm.Library.Filter);
            var list = view.FindControl<ListBox>("QueueTracks")!;
            var duplicateItem = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext is QueueTrackViewModel { Id: var id } && id == duplicate.Id);
            Click(duplicateItem);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (vm.Playback.IsBusy) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5, timeout.Token); }
            Layout();
            Assert.Equal(duplicate.Id, session.Queue.CurrentItem!.Id);
            Assert.Equal(duplicate.Id, Assert.Single(drawer.Tracks, r => r.IsCurrent).Id);
            Assert.Equal(new[] { first.Id, second.Id, duplicate.Id }, session.Queue.Items.Select(i => i.Id));
            Assert.Equal(request, session.Queue.Request);
            Assert.True(popup.IsOpen);

            window.Width = 1180; window.Height = 790;
            await appearance.SetAsync(new(ThemePreference.Dark, LanguagePreference.Chinese, true));
            Layout();
            Assert.Equal("3 首歌曲", drawer.TrackCount);
            Assert.Equal(window.Bounds.Height, card.Bounds.Height);
            Assert.InRange(Math.Abs(card.TranslatePoint(default, window)!.Value.X + card.Bounds.Width - window.Bounds.Width), 0, 1);
            SavePreview("Queue-Dark-1180");
            var blankInside = card.TranslatePoint(new(24, card.Bounds.Height - 100), window)!.Value;
            window.MouseDown(blankInside, MouseButton.Left); window.MouseUp(blankInside, MouseButton.Left);
            Assert.True(popup.IsOpen);
            ClickOutside();
            Assert.False(popup.IsOpen); Assert.False(vm.IsQueueOpen);
            Assert.Same(currentPage, vm.Navigation.Current);
            Assert.Equal(historyDepth, vm.Navigation.Host.StackDepth);
            Click(queueButton);
            Assert.Same(drawer, vm.QueueDrawer);
            Assert.Equal(first.Track.Title, drawer.Filter);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Layout();
            Assert.False(popup.IsOpen);
            Click(queueButton);
            Click(view.FindControl<Button>("ClearQueueButton")!);
            await vm.Playback.ClearQueueCommand.ExecutionTask!;
            Layout();
            Assert.Empty(session.Queue.Items); Assert.Empty(drawer.Tracks);
            Assert.False(drawer.HasTracks); Assert.False(drawer.CanClearQueue);
            Assert.True(popup.IsOpen);
            Click(window.FindControl<Button>("CloseQueueButton")!);
            Assert.False(popup.IsOpen);

            window.Width = 880; window.Height = 620;
            await appearance.SetAsync(new(ThemePreference.Light, LanguagePreference.English, false));
            Layout(); Click(queueButton);
            Assert.IsType<TranslateTransform>(card.RenderTransform);
            ClickOutside();
            Layout();
            Assert.Null(card.RenderTransform); Assert.Equal(1, card.Opacity);
            Click(queueButton);
            var sampled = false;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (card.RenderTransform is TranslateTransform transform)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    if (transform.X > 0 && card.Opacity is > 0 and < 1) sampled = true;
                    await Task.Delay(5, timeout.Token);
                }
            Assert.True(sampled);
            Assert.Equal(1, card.Opacity);
            SavePreview("Queue-Light-880");
        }
        finally { vm.CloseQueueCommand.Execute(null); window.Close(); }

        void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        void Click(Control control)
        {
            Layout();
            var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
        }
        void ClickOutside()
        {
            var point = new Point(300, 450);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
        }
        void SavePreview(string name)
        {
            if (Environment.GetEnvironmentVariable("LEMONNEO_PREVIEW_DIRECTORY") is not { Length: > 0 } directory) return;
            Directory.CreateDirectory(directory);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
            bitmap.Render(window); bitmap.Save(Path.Combine(directory, $"{name}.png"), PngBitmapEncoderOptions.Default);
        }
    }
}
