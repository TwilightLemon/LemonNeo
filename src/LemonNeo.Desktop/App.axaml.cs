using LemonNeo.Abstractions.Settings;
using LemonNeo.Hosting;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shell;
using LemonNeo.Desktop.Views.Shell;

namespace LemonNeo.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Use platform window controls, menus, file pickers, and font fallback.
            var paths = AppHost.CreateDefaultPaths();
            var host = new DesktopHost(paths, DesktopOptions.Parse(Program.Arguments, paths));
            var settings = host.Settings.ForPackage(PackageNames.Desktop);
            DesktopPreferences.Register(settings);
            var text = new LocalizationService(settings);
            var appearance = new AppearanceService(this, settings);
            var viewModel = new MainViewModel(host, text, appearance, host.Get<ArtworkService>());
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            DesktopApplicationMenu? menu = null;
            async Task ShutdownAsync()
            {
                menu?.Dispose();
                window.CloseDesktopLyrics();
                try
                {
                    await viewModel.DisposeAsync();
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.WriteLine($"Desktop shutdown failed: {exception}");
                }
                finally
                {
                    try { await host.DisposeAsync(); }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Trace.WriteLine($"Desktop plugin shutdown failed: {exception}");
                    }
                    appearance.Dispose();
                    text.Dispose();
                }
            }
            var lifetime = new DesktopLifetimeService(desktop, window,
                keepRunningOnClose: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
                viewModel.InitializeAsync, ShutdownAsync, this.TryGetFeature<IActivatableLifetime>(),
                window.PrepareForBackground);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                menu = new DesktopApplicationMenu(this, lifetime, viewModel.Playback, text,
                    useTrayIcon: OperatingSystem.IsWindows());
        }
        base.OnFrameworkInitializationCompleted();
    }
}
