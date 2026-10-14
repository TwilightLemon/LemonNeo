Desktop packages for Windows (x64) and macOS (Apple silicon).

Both platforms ship two variants:

- `-self-contained` bundles the .NET runtime, so there is nothing to install first.
- `-framework-dependent` requires the [.NET 10 desktop runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

Windows downloads are ZIP archives. macOS downloads are DMG disk images: open one,
then drag `Lemon Neo.app` onto the `Applications` shortcut.

Plugins live in the `plugins/` folder next to the app; deleting one of those folders
uninstalls that plugin. The macOS builds are not signed with a valid Apple Developer ID,
so Gatekeeper may refuse the first launch. If the app will not open:

1. Clear the quarantine attribute:
   `xattr -dr com.apple.quarantine "/Applications/Lemon Neo.app"`
2. Or open **System Settings → Privacy & Security** and choose **Open Anyway**.
