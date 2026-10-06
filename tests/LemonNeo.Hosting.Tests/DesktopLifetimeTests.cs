using AvaloniaApplication = Avalonia.Application;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Application.Playback;
using LemonNeo.Desktop;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shell;
using LemonNeo.Desktop.Views.Shell;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class DesktopLifetimeTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrayAndDockKeepPlaybackAndWindowStateAcrossReopens(bool useTrayIcon)
    {
        using var paths = new TemporaryPaths();
        var player = new TestPlayer();
        await using var host = new DesktopHost(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory),
            [player], [new TestMusic()]);
        var scope = host.Settings.ForPackage(PackageNames.Desktop);
        DesktopPreferences.Register(scope);
        using var text = new LocalizationService(scope);
        text.Apply(LanguagePreference.English);
        using var appearance = new AppearanceService(AvaloniaApplication.Current!, scope);
        await using var main = new MainViewModel(host, text, appearance, host.Get<ArtworkService>());
        await main.InitializeAsync();
        var window = new MainWindow { DataContext = main };
        var desktop = new TestDesktopLifetime { MainWindow = window };
        var startups = 0;
        var shutdowns = 0;
        var closed = 0;
        window.Closed += (_, _) => closed++;
        using var lifetime = new DesktopLifetimeService(desktop, window, true,
            () => { startups++; return Task.CompletedTask; },
            () => { shutdowns++; return Task.CompletedTask; }, prepareForBackground: window.PrepareForBackground,
            exitApplication: () => desktop.Shutdown());
        using var applicationMenu = new DesktopApplicationMenu(AvaloniaApplication.Current!, lifetime, main.Playback,
            text, useTrayIcon);
        window.Show();
        try
        {
            var menu = useTrayIcon
                ? Assert.Single(TrayIcon.GetIcons(AvaloniaApplication.Current!)!).Menu!
                : NativeDock.GetMenu(AvaloniaApplication.Current!)!;
            Assert.NotNull(menu);
            if (useTrayIcon) Assert.NotNull(Assert.Single(TrayIcon.GetIcons(AvaloniaApplication.Current!)!).Icon);
            var show = Assert.IsType<NativeMenuItem>(menu.Items[0]);
            var previous = Assert.IsType<NativeMenuItem>(menu.Items[2]);
            var toggle = Assert.IsType<NativeMenuItem>(menu.Items[3]);
            var next = Assert.IsType<NativeMenuItem>(menu.Items[4]);
            var exit = Assert.IsType<NativeMenuItem>(menu.Items[6]);
            Assert.False(toggle.IsEnabled);
            var session = host.Get<PlaybackSessionService>();
            await session.PlayPlaylistAsync([HostFixture.Track(), HostFixture.Track("two")],
                cancellationToken: TestContext.Current.CancellationToken);
            await session.SeekAsync(TimeSpan.FromSeconds(35), TestContext.Current.CancellationToken);
            main.Playback.UpdateSnapshot();
            var queue = session.Queue.Items.ToArray();
            var navigation = main.Navigation.Current;
            main.ToggleQueueCommand.Execute(null);
            Assert.True(main.IsQueueOpen);

            window.Close();
            Assert.False(window.IsVisible);
            Assert.False(main.IsQueueOpen);
            Assert.Equal(0, closed);
            Assert.Equal(0, shutdowns);
            Assert.Equal(PlayerState.Playing, player.State);
            Assert.Equal(TimeSpan.FromSeconds(35), player.Timeline.Position);
            Assert.True(toggle.IsEnabled);
            Assert.Equal("Pause", toggle.Header);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(toggle.Command).ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PlayerState.Paused, player.State);
            Assert.Equal("Play", toggle.Header);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(toggle.Command).ExecuteAsync(null);
            Assert.Equal(PlayerState.Playing, player.State);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(next.Command).ExecuteAsync(null);
            Assert.Equal("two", player.CurrentTrack!.Title);
            Assert.True(previous.IsEnabled);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(previous.Command).ExecuteAsync(null);
            Assert.Equal("one", player.CurrentTrack!.Title);

            text.Apply(LanguagePreference.Chinese);
            Assert.Equal("显示 Lemon Neo", show.Header);
            Assert.Equal("暂停", toggle.Header);
            Assert.Equal("退出 Lemon Neo", exit.Header);
            for (var i = 0; i < 3; i++)
            {
                show.Command!.Execute(null);
                Assert.True(window.IsVisible);
                Assert.Same(navigation, main.Navigation.Current);
                Assert.Equal(queue, session.Queue.Items);
                window.Close();
                Assert.False(window.IsVisible);
            }
            lifetime.ShowMainWindow();
            Assert.True(window.IsVisible);
            window.WindowState = WindowState.Minimized;
            window.Close();
            show.Command!.Execute(null);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(1, startups);
            Assert.Equal(0, closed);
            Assert.Equal(ShutdownMode.OnExplicitShutdown, desktop.ShutdownMode);
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(exit.Command).ExecuteAsync(null);
            Assert.Equal(1, closed);
            Assert.Equal(1, shutdowns);
            Assert.Equal(1, desktop.ShutdownCalls);
        }
        finally
        {
            lifetime.Dispose();
            window.Close();
        }
        applicationMenu.Dispose();
        Assert.Null(TrayIcon.GetIcons(AvaloniaApplication.Current!));
        Assert.Null(NativeDock.GetMenu(AvaloniaApplication.Current!));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeQuitWaitsForCleanupAndRejectsReopens(bool hidden)
    {
        var window = new Window();
        var desktop = new TestDesktopLifetime { MainWindow = window };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCalls = 0;
        using var lifetime = new DesktopLifetimeService(desktop, window, true, () => Task.CompletedTask,
            async () => { cleanupCalls++; entered.SetResult(); await release.Task; },
            exitApplication: () => desktop.Shutdown());
        window.Show();
        if (hidden) window.Close();
        try
        {
            Assert.False(desktop.TryShutdown());
            var shutdown = lifetime.ShutdownAsync();
            Assert.Same(shutdown, lifetime.ShutdownAsync());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(shutdown.IsCompleted);
            Assert.False(window.IsEnabled);
            Assert.Equal(0, desktop.ShutdownCalls);
            lifetime.ShowMainWindow();
            window.Close();
            Assert.Equal(!hidden, window.IsVisible);
            Assert.Equal(1, cleanupCalls);
            release.SetResult();
            await shutdown;
            Assert.Equal(1, desktop.ShutdownCalls);
            Assert.Null(window.PlatformImpl);
            lifetime.ShowMainWindow();
            Assert.False(window.IsVisible);
        }
        finally
        {
            release.TrySetResult();
            await lifetime.ShutdownAsync();
        }
    }

    [AvaloniaFact]
    public async Task CleanupFailureStillExits()
    {
        var window = new Window();
        var desktop = new TestDesktopLifetime { MainWindow = window };
        using var lifetime = new DesktopLifetimeService(desktop, window, true, () => Task.CompletedTask,
            () => Task.FromException(new IOException("Shutdown test failure.")),
            exitApplication: () => desktop.Shutdown());
        window.Show();
        window.Close();
        await lifetime.ShutdownAsync();
        Assert.Equal(1, desktop.ShutdownCalls);
        Assert.Null(window.PlatformImpl);
    }

    [AvaloniaFact]
    public async Task PlatformsWithoutBackgroundEntryPointExitOnWindowClose()
    {
        var window = new Window();
        var desktop = new TestDesktopLifetime { MainWindow = window };
        using var lifetime = new DesktopLifetimeService(desktop, window, false,
            () => Task.CompletedTask, () => Task.CompletedTask, exitApplication: () => desktop.Shutdown());
        window.Show();
        window.Close();
        await lifetime.ShutdownAsync();
        Assert.Equal(1, desktop.ShutdownCalls);
        Assert.Null(window.PlatformImpl);
    }

    private sealed class TestDesktopLifetime : ClassicDesktopStyleApplicationLifetime
    {
        public int ShutdownCalls { get; private set; }

        public new void Shutdown(int exitCode = 0)
        {
            ShutdownCalls++;
            MainWindow?.Close();
        }
    }
}
