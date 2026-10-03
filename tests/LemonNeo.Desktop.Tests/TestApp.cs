using Avalonia;
using Avalonia.Headless;
using LemonNeo.Desktop;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(LemonNeo.Desktop.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LemonNeo.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
