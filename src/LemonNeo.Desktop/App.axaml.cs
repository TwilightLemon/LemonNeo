using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.Views.Shell;
using LemonNeo.Hosting;

namespace LemonNeo.Desktop;

public class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsWindows())
            Resources["Font.UI"] = new FontFamily("Microsoft YaHei UI");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Use platform window controls, menus, file pickers, and font fallback.
            var paths = AppHost.CreateDefaultPaths();
            var window = new MainWindow();
            desktop.MainWindow = window;
            var applicationService = new DesktopApplicationService(this, window, paths);
            DesktopApplicationMenu? menu = null;
            DesktopLifetimeService? lifetime = null;

            async Task ShutdownAsync()
            {
                menu?.Dispose();
                menu = null;
                try
                {
                    await applicationService.DisposeAsync();
                }
                catch (Exception exception)
                {
                    Trace.WriteLine($"Desktop shutdown failed: {exception}");
                }
            }

            async Task InitializeAsync()
            {
                if (!await applicationService.InitializeAsync())
                {
                    desktop.Shutdown();
                    return;
                }

                if (lifetime is not null && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
                    menu = new DesktopApplicationMenu(this, lifetime, applicationService.ViewModel.Playback,
                        applicationService.Text,
                        OperatingSystem.IsWindows());
            }

            lifetime = new DesktopLifetimeService(desktop, window,
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
                InitializeAsync, ShutdownAsync, this.TryGetFeature<IActivatableLifetime>(),
                window.PrepareForBackground);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
