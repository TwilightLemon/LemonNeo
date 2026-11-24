using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.Views.Accounts;

internal sealed class WebLoginWindow : Window
{
    private readonly NativeWebView _webView = new();
    private readonly IWebAuthenticationProvider _provider;
    private readonly IReadOnlyList<WebLoginOption> _options;
    private readonly ComboBox _methods;
    private readonly LocalizationService _text;
    private Uri? _callback;

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public WebLoginWindow(IWebAuthenticationProvider provider, LocalizationService text, string providerName,
        string profileDirectory)
    {
        _provider = provider;
        _text = text;
        _options = provider.CreateWebLoginOptions();
        if (_options.Count == 0) throw new ProviderException(text[LanguageManager.Keys.WebLoginUnavailable]);

        Title = text.Format(LanguageManager.Keys.WebLoginTitle, providerName);
        Width = 900;
        Height = 680;
        MinWidth = 520;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _methods = new ComboBox
        {
            ItemsSource = _options.Select(option => option.Name).ToArray(),
            SelectedIndex = 0,
            IsVisible = _options.Count > 1,
            MinWidth = 120
        };
        var cancel = new Button
        {
            Content = text[LanguageManager.Keys.CancelOperation],
            HorizontalAlignment = HorizontalAlignment.Right
        };
        cancel.Click += (_, _) => Close();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12) };
        Grid.SetColumn(cancel, 1);
        header.Children.Add(_methods);
        header.Children.Add(cancel);
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(_webView, 1);
        content.Children.Add(header);
        content.Children.Add(_webView);
        Content = content;

        _webView.EnvironmentRequested += (_, args) =>
        {
            args.EnableDevTools = false;
            if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.IsInPrivateModeEnabled = true;
                windows.UserDataFolder = profileDirectory;
            }
            if (args is AppleWKWebViewEnvironmentRequestedEventArgs mac)
                mac.NonPersistentDataStore = true;
        };
        _webView.NavigationStarted += (_, args) =>
        {
            if (args.Request is not { } uri) return;
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                args.Cancel = true;
                return;
            }
            if (_methods.SelectedIndex < 0 || _options[_methods.SelectedIndex].CallbackUri is not { } endpoint ||
                Uri.Compare(uri, endpoint, UriComponents.SchemeAndServer | UriComponents.Path,
                    UriFormat.SafeUnescaped, StringComparison.Ordinal) != 0) return;
            args.Cancel = true;
            _callback ??= uri;
            _methods.IsEnabled = false;
        };
        _webView.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.Request is { } uri && uri.Scheme == Uri.UriSchemeHttps)
                Dispatcher.UIThread.Post(() => _webView.Navigate(uri));
        };
        _webView.Source = _options[0].StartUri;
        _methods.SelectionChanged += (_, _) =>
        {
            if (_methods.SelectedIndex < 0) return;
            _callback = null;
            _webView.Navigate(_options[_methods.SelectedIndex].StartUri);
        };
    }

    public async Task<AuthenticationState?> SignInAsync(Window owner, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Closed += (_, _) => lifetime.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = ShowDialog(owner);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible) Close();
        }));

        try
        {
            var started = DateTimeOffset.UtcNow;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var option = _options[_methods.SelectedIndex];
                if (option.CallbackUri is not null && _callback is null &&
                    _webView.TryGetCookieManager() is not null) continue;

                var manager = _webView.TryGetCookieManager();
                if (manager is null)
                {
                    if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(15))
                        throw new ProviderException(_text[LanguageManager.Keys.WebLoginUnavailable]);
                    continue;
                }

                var cookies = await manager.GetCookiesAsync().WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
                if (option != _options[_methods.SelectedIndex]) continue;
                var state = await _provider.CompleteWebLoginAsync(new WebLoginRequest(option, _callback, cookies), lifetime.Token);
                if (state is { IsAuthenticated: true }) return state;
            }
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (IsVisible) Close();
            await dialog;
        }
    }
}
