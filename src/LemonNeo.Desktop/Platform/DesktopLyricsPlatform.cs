using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace LemonNeo.Desktop.Platform;

/// <summary>Platform behavior for an interactive floating lyric window.</summary>
internal static class DesktopLyricsPlatform
{
    private const uint NoActivate = 0x08000000, ToolWindow = 0x00000080, AppWindow = 0x00040000;
    private const nuint CanJoinAllSpaces = 1, FullScreenPrimary = 1 << 7, FullScreenAuxiliary = 1 << 8;

    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";

    public static void Configure(Window window)
    {
        if (OperatingSystem.IsWindows())
            // Keep style flags through Avalonia updates; do not activate the other app's input focus on click.
            Win32Properties.AddWindowStylesCallback(window, (style, extended) =>
                (style, (extended | NoActivate | ToolWindow) & ~AppWindow));
    }

    public static void OnOpened(Window window)
    {
        if (!OperatingSystem.IsMacOS() ||
            window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle { NSWindow: var handle } ||
            handle == IntPtr.Zero) return;
        // FullScreenPrimary and FullScreenAuxiliary are mutually exclusive. Join Spaces and coexist with fullscreen apps.
        var behavior = SendGet(handle, Selector("collectionBehavior"));
        SendSet(handle, Selector("setCollectionBehavior:"),
            (behavior & ~FullScreenPrimary) | CanJoinAllSpaces | FullScreenAuxiliary);
        SendSet(handle, Selector("setHidesOnDeactivate:"), 0);
        SendSet(handle, Selector("setHasShadow:"), 0);
        // Avalonia's native window exposes this setter. Check it before calling across backend versions.
        var keyWindowSelector = Selector("setCanBecomeKeyWindow:");
        if (SendResponds(handle, Selector("respondsToSelector:"), keyWindowSelector))
            SendSet(handle, keyWindowSelector, 0);
    }

    public static void SetSettingsInputEnabled(Window window, bool enabled)
    {
        // Normal playback stays non-activating. Font editing explicitly needs keyboard/IME focus.
        if (OperatingSystem.IsMacOS() &&
            window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle { NSWindow: var handle } &&
            handle != IntPtr.Zero)
        {
            var selector = Selector("setCanBecomeKeyWindow:");
            if (SendResponds(handle, Selector("respondsToSelector:"), selector))
                SendSet(handle, selector, enabled ? 1u : 0u);
        }

        if (enabled) window.Activate();
    }

    private static IntPtr Selector(string name)
    {
        return RegisterSelector(name);
    }

    [DllImport(ObjectiveC, EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterSelector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nuint SendGet(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendResponds(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SendSet(IntPtr receiver, IntPtr selector, nuint value);
}
