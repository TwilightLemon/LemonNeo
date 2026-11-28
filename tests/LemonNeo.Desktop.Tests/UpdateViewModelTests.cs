using System.Net;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Shared;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class UpdateViewModelTests
{
    [Fact]
    public async Task DismissesBannerWithoutLosingAvailableUpdate()
    {
        using var text = new LocalizationService();
        using var client = CreateClient(_ => Task.FromResult(NewRelease()));
        await using var model = new UpdateViewModel(client, new UpdateSettings(), text);

        await model.CheckAsync();
        Assert.True(model.IsUpdateAvailable);
        Assert.True(model.ShowNotification);
        Assert.Contains("v9999.0.0", model.AvailableVersionText);
        model.DismissCommand.Execute(null);
        Assert.False(model.ShowNotification);
        Assert.True(model.IsUpdateAvailable);

        await model.CheckAsync();
        Assert.False(model.ShowNotification);
        Assert.False(model.IsChecking);
    }

    [Fact]
    public async Task DisablingStartupChecksStillAllowsManualChecks()
    {
        var requests = 0;
        using var text = new LocalizationService();
        var settings = new UpdateSettings();
        using var client = CreateClient(_ =>
        {
            requests++;
            return Task.FromResult(NewRelease());
        });
        await using var model = new UpdateViewModel(client, settings, text);
        Assert.True(model.CheckOnStartup);

        model.CheckOnStartup = false;
        model.CheckAtStartup();
        Assert.False(settings.Get<UpdatePreferences>().CheckOnStartup);
        Assert.Equal(0, requests);

        await model.CheckCommand.ExecuteAsync(null);
        Assert.Equal(1, requests);
        Assert.True(model.ShowNotification);
    }

    [Fact]
    public async Task NetworkFailuresAreReportedWithoutThrowingOrShowingBanner()
    {
        using var text = new LocalizationService();
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        await using var model = new UpdateViewModel(client, new UpdateSettings(), text);

        await model.CheckAsync();

        Assert.Equal(text[LanguageManager.Keys.UpdateCheckFailed], model.Status);
        Assert.False(model.IsUpdateAvailable);
        Assert.False(model.ShowNotification);
        Assert.False(model.IsChecking);
        Assert.True(model.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartupReturnsWhileCheckIsPendingAndConcurrentChecksShareOneRequest()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var text = new LocalizationService();
        using var client = CreateClient(_ =>
        {
            requests++;
            return response.Task;
        });
        await using var model = new UpdateViewModel(client, new UpdateSettings(), text);

        model.CheckAtStartup();
        var check = model.CheckAsync();
        var duplicate = model.CheckAsync();
        Assert.Same(check, duplicate);
        Assert.Equal(1, requests);
        Assert.True(model.IsChecking);
        Assert.False(model.CheckCommand.CanExecute(null));

        response.SetResult(NewRelease());
        await check;
        Assert.True(model.ShowNotification);
        Assert.True(model.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task ShutdownCancelsPendingCheckAndPreventsFurtherRequests()
    {
        var cancelled = false;
        var requests = 0;
        using var text = new LocalizationService();
        using var client = CreateClient(async cancellationToken =>
        {
            requests++;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return NewRelease();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
        });
        var model = new UpdateViewModel(client, new UpdateSettings(), text);
        var check = model.CheckAsync();

        await model.DisposeAsync();
        await check;
        await model.CheckAsync();

        Assert.True(cancelled);
        Assert.Equal(1, requests);
        Assert.False(model.IsChecking);
        Assert.False(model.CheckCommand.CanExecute(null));
    }

    private static GitHubReleaseClient CreateClient(Func<CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new StubHandler(respond)));

    private static HttpResponseMessage NewRelease() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"tag_name":"v9999.0.0"}""")
    };

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(cancellationToken);
    }

    private sealed class UpdateSettings : ISettingsScope
    {
        private UpdatePreferences _preferences = new();
        private event Action<UpdatePreferences>? Changed;
        public string PackageId => PackageNames.Desktop;
        public T Get<T>() where T : class => (T)(object)_preferences;
        public void Apply<T>(Func<T, T> update) where T : class
        {
            _preferences = (UpdatePreferences)(object)update(Get<T>());
            Changed?.Invoke(_preferences);
        }

        public IDisposable Subscribe<T>(Action<T> onChanged) where T : class
        {
            void Notify(UpdatePreferences value) => onChanged((T)(object)value);
            Changed += Notify;
            return new Subscription(() => Changed -= Notify);
        }

        public void Register<T>(string entityName, T defaults, Func<T, T>? normalize = null) where T : class =>
            throw new NotSupportedException();

        public Task RegisterAsync<T>(string entityName, T defaults, Func<T, T>? normalize = null,
            CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

        private sealed class Subscription(Action unsubscribe) : IDisposable
        {
            public void Dispose() => unsubscribe();
        }
    }
}
