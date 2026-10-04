using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LemonNeo.Configuration;
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
            var paths = AppPathProvider.CreateDefault();
            var runtime = new DesktopRuntime(paths, DesktopOptions.Parse(Program.Arguments, paths));
            var text = new LocalizationService();
            var appearance = new AppearanceService(this, text, runtime.Get<IAppConfigurationStore>());
            var viewModel = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
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
                    try { await runtime.DisposeAsync(); }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Trace.WriteLine($"Desktop plugin shutdown failed: {exception}");
                    }
                    shutdownComplete = true;
                    window.Close();
                }
            }
            window.Closing += Shutdown;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
