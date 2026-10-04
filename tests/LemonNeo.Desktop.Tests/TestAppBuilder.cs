using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(LemonNeo.Desktop.Tests.TestAppBuilder))]

namespace LemonNeo.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
