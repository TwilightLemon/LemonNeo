using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Desktop;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shell;
using LemonNeo.Desktop.Views.Accounts;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class DesktopAccountsPluginInformationTests
{
    [AvaloniaFact]
    public async Task AccountsIdentifiesThePluginProvidingTheSelectedSignInService()
    {
        using var paths = new TemporaryPaths();
        await VerifyPluginAttributionAsync(paths);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task VerifyPluginAttributionAsync(TemporaryPaths paths)
    {
        var pluginDirectory = Path.Combine(paths.PluginDirectory, "QqMusic");
        Directory.CreateDirectory(pluginDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "LemonNeo.Plugin.QqMusic.dll"),
            Path.Combine(pluginDirectory, "LemonNeo.Plugin.QqMusic.dll"));
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "plugin.json"), """
            {
              "id": "LemonNeo.Plugin.QqMusic",
              "name": "QQ Music User Library",
              "version": "1.0.0",
              "minimumHostVersion": "0.1.0",
              "entryAssembly": "LemonNeo.Plugin.QqMusic.dll"
            }
            """, TestContext.Current.CancellationToken);

        await using var host = new DesktopHost(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory));
        var scope = host.Settings.ForPackage(PackageNames.Desktop);
        DesktopPreferences.Register(scope);
        using var text = new LocalizationService(scope);
        text.Apply(LanguagePreference.English);
        using var appearance = new AppearanceService(Avalonia.Application.Current!, scope);
        await using var main = new MainViewModel(host, text, appearance, host.Get<ArtworkService>());
        await main.InitializeAsync();

        var accounts = main.Accounts;
        Assert.Equal("qqmusic", Assert.Single(accounts.Services).Id);
        Assert.True(accounts.HasPluginAttribution);
        Assert.Equal("Sign-in provided by the QQ Music User Library 1.0.0 plugin.", accounts.PluginAttribution);

        var view = new AccountsView { DataContext = accounts };
        var window = new Window { Content = view, Width = 720, Height = 480 };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var attribution = view.FindControl<TextBlock>("AuthenticationPluginAttribution")!;
            Assert.True(attribution.IsVisible);
            Assert.Equal(accounts.PluginAttribution, attribution.Text);

            text.Apply(LanguagePreference.Chinese);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("登录功能由 QQ Music User Library 1.0.0 插件提供。", accounts.PluginAttribution);
            Assert.Equal(accounts.PluginAttribution, attribution.Text);
        }
        finally { window.Close(); }
    }
}
