using Avalonia;
using Avalonia.Headless;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(LemonNeo.Tests.TestApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LemonNeo.Tests;

public sealed class TestApp : Avalonia.Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<LemonNeo.Desktop.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
