using LemonNeo.Abstractions.Settings;
using LemonNeo.Hosting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;

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
            window.Opened += async (_, _) => await viewModel.InitializeAsync();

            var shutdownStarted = false;
            var shutdownComplete = false;
            async void Shutdown(object? sender, WindowClosingEventArgs args)
            {
                if (shutdownComplete) return;
                args.Cancel = true;
                if (shutdownStarted) return;
                shutdownStarted = true;
                window.IsEnabled = false;
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
                    shutdownComplete = true;
                    window.Close();
                }
            }
            window.Closing += Shutdown;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
