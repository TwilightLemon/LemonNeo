using Avalonia;
using Avalonia.Media;

namespace LemonNeo.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var options = new FontManagerOptions();

        if (OperatingSystem.IsWindows())
        {
            // Windows 平台：优先使用 PingFang SC（若用户安装），回退到 Microsoft YaHei UI
            // FontFamily 字符串支持逗号分隔的回退链，Avalonia 会按顺序查找可用字体
            options.DefaultFamilyName = "PingFang SC, Microsoft YaHei UI, Microsoft YaHei";
            options.FontFallbacks =[new FontFallback { FontFamily = new FontFamily("Microsoft YaHei") }];
        }
        // 其他系统（macOS/Linux）不设置 DefaultFamilyName，使用系统默认字体

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(options)
            .LogToTrace();
    }
}