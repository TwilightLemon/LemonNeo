using System.Diagnostics;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Core.Exceptions;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.ViewModels.Shell;
using LemonNeo.Desktop.Views.Shared;
using LemonNeo.Desktop.Views.Shell;

namespace LemonNeo.Desktop.Services;

/// <summary>Owns the single Desktop host from application initialization through shutdown.</summary>
public sealed class DesktopApplicationService : IDesktopStorageService, IAsyncDisposable
{
    private readonly IAppPathProvider _paths;
    private readonly ISettingsScope _settings;
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private readonly MainWindow _window;
    private AppearanceService? _appearance;
    private DesktopHost? _host;
    private bool _initialized;
    private LocalizationService? _text;
    private MainViewModel? _viewModel;

    public DesktopApplicationService(Avalonia.Application application, MainWindow window, IAppPathProvider paths)
    {
        _window = window;
        _paths = paths;

        var host = new DesktopHost(paths, paths.MusicLibraryDirectory, paths.CacheDirectory, this);
        var settings = host.Settings.ForPackage(PackageNames.Desktop);
        DesktopPreferences.Register(settings);
        var text = new LocalizationService(settings);
        var appearance = new AppearanceService(application, settings);
        var viewModel = new MainViewModel(host, text, appearance, host.Get<ArtworkService>());

        _host = host;
        _settings = settings;
        _text = text;
        _appearance = appearance;
        _viewModel = viewModel;
        window.DataContext = viewModel;
        window.Title = text[LanguageManager.Keys.AppName];
    }

    public MainViewModel ViewModel =>
        _viewModel ?? throw new ObjectDisposedException(nameof(DesktopApplicationService));

    public LocalizationService Text => _text ?? throw new ObjectDisposedException(nameof(DesktopApplicationService));

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _storageGate.Dispose();
    }

    public async Task ChangeLibraryAsync(string directory, CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
        _window.IsEnabled = false;
        try
        {
            var host = _host ?? throw new ObjectDisposedException(nameof(DesktopApplicationService));
            var viewModel = _viewModel ?? throw new ObjectDisposedException(nameof(DesktopApplicationService));
            var next = ValidateLibraryDirectory(directory, host.StorageLocations.CacheDirectory);
            if (PathEquals(next, host.StorageLocations.LibraryDirectory)) return;

            await host.ChangeLibraryDirectoryAsync(next, cancellationToken);
            viewModel.SharedContext.RefreshLibrary();
            _window.Title = $"{Text[LanguageManager.Keys.AppName]} — {new DirectoryInfo(next).Name}";
            await SaveStoragePreferencesAsync(host, cancellationToken);
        }
        finally
        {
            _window.IsEnabled = true;
            _storageGate.Release();
        }
    }

    public async Task ChangeCacheAsync(string directory, CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
        _window.IsEnabled = false;
        try
        {
            var host = _host ?? throw new ObjectDisposedException(nameof(DesktopApplicationService));
            var next = ValidateCacheDirectory(directory, host.StorageLocations.LibraryDirectory);
            if (PathEquals(next, host.StorageLocations.CacheDirectory)) return;

            host.ChangeCacheDirectory(next);
            await SaveStoragePreferencesAsync(host, cancellationToken);
        }
        finally
        {
            _window.IsEnabled = true;
            _storageGate.Release();
        }
    }

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _storageGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return true;
            var host = _host ?? throw new ObjectDisposedException(nameof(DesktopApplicationService));
            var viewModel = _viewModel ?? throw new ObjectDisposedException(nameof(DesktopApplicationService));
            viewModel.BeginStartup();

            try
            {
                // Desktop-bound configuration notifications must stay on the UI context.
                await host.Settings.LoadAsync(cancellationToken);
                var preferences = _settings.Get<DesktopStoragePreferences>();
                var cacheDirectory = ResolveCacheDirectory(preferences.CacheDirectory);
                var hasConfiguredLibrary = !string.IsNullOrWhiteSpace(preferences.LibraryDirectory);
                var libraryDirectory = hasConfiguredLibrary
                    ? preferences.LibraryDirectory!
                    : _paths.MusicLibraryDirectory;

                ValidatedStoragePaths storage;
                var usingConfiguredLibrary = hasConfiguredLibrary;
                try
                {
                    storage = ValidateInitialStorage(libraryDirectory, cacheDirectory);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && hasConfiguredLibrary)
                {
                    await ReportLibraryFallbackAsync(exception);
                    storage = ValidateInitialStorage(_paths.MusicLibraryDirectory, _paths.CacheDirectory);
                    usingConfiguredLibrary = false;
                }

                host.ConfigureInitialStorage(storage.LibraryDirectory, storage.CacheDirectory);
                try
                {
                    await InitializeHostInBackgroundAsync(host, cancellationToken);
                }
                catch (LibraryException exception) when (usingConfiguredLibrary)
                {
                    await ReportLibraryFallbackAsync(exception);
                    storage = ValidateInitialStorage(_paths.MusicLibraryDirectory, _paths.CacheDirectory);
                    host.ChangeCacheDirectory(storage.CacheDirectory);
                    await host.ChangeLibraryDirectoryAsync(storage.LibraryDirectory, cancellationToken);
                    await InitializeHostInBackgroundAsync(host, cancellationToken);
                }

                await viewModel.CompleteStartupAsync();
                _window.Title = $"{Text[LanguageManager.Keys.AppName]} — {new DirectoryInfo(host.StorageLocations.LibraryDirectory).Name}";
                _window.RestoreDesktopLyrics();
                _initialized = true;
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Desktop initialization failed: {exception}");
                viewModel.ReportStartupFailure(exception.Message);
                await ShowErrorAsync(Text[LanguageManager.Keys.DesktopStartupFailedTitle], exception.Message);
                return false;
            }
        }
        finally
        {
            _storageGate.Release();
        }
    }

    private static Task InitializeHostInBackgroundAsync(DesktopHost host, CancellationToken cancellationToken)
    {
        return Task.Run(() => host.InitializeAsync(cancellationToken), cancellationToken);
    }

    private async Task ReportLibraryFallbackAsync(Exception exception)
    {
        Trace.WriteLine($"Could not open the configured music library: {exception}");
        await ShowErrorAsync(Text[LanguageManager.Keys.LibraryOpenFailedTitle], exception.Message);
    }

    private ValidatedStoragePaths ValidateInitialStorage(string libraryDirectory, string cacheDirectory)
    {
        var cache = ValidateCacheDirectory(cacheDirectory);
        var library = ValidateLibraryDirectory(libraryDirectory, cache);
        EnsureSeparate(cache, library, StorageSeparation("CachePath", "LibraryPath"));
        return new ValidatedStoragePaths(library, cache);
    }

    private string ValidateLibraryDirectory(string directory, string cacheDirectory)
    {
        var library = NormalizeDirectory(directory);
        var usesPlatformDefaultLibrary = PathEquals(
            library, NormalizeDirectory(_paths.MusicLibraryDirectory));

        EnsureSeparate(library, cacheDirectory, StorageSeparation("LibraryPath", "CachePath"));
        if (!usesPlatformDefaultLibrary)
            EnsureSeparate(library, _paths.ConfigurationDirectory,
                StorageSeparation("LibraryPath", "ConfigurationPath"));
        EnsureSeparate(library, _paths.DataProtectionKeysDirectory,
            StorageSeparation("LibraryPath", "DataProtectionKeysPath"));
        EnsureSeparate(library, Path.Combine(_paths.DataDirectory, "Credentials"),
            StorageSeparation("LibraryPath", "CredentialsPath"));
        EnsureSeparate(library, _paths.PluginDirectory, StorageSeparation("LibraryPath", "PluginPath"));

        Directory.CreateDirectory(library);
        if (!File.Exists(Path.Combine(library, "library.json")) && Directory.EnumerateFileSystemEntries(library).Any())
            throw new InvalidOperationException(Text[LanguageManager.Keys.LibraryFolderInvalid]);

        VerifyWritable(library);
        return library;
    }

    private string ResolveCacheDirectory(string? configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory)) return _paths.CacheDirectory;
        try
        {
            var cache = NormalizeDirectory(configuredDirectory);
            ValidateCacheDirectory(cache);
            return cache;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Ignoring invalid cache preference: {exception}");
            return _paths.CacheDirectory;
        }
    }

    public async Task ShutdownAsync()
    {
        await _storageGate.WaitAsync();
        try
        {
            var viewModel = _viewModel;
            var host = _host;
            var appearance = _appearance;
            var text = _text;
            _viewModel = null;
            _host = null;
            _appearance = null;
            _text = null;
            _window.CloseDesktopLyrics();
            _window.DataContext = null;
            try
            {
                if (viewModel is not null) await viewModel.DisposeAsync();
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Desktop view-model shutdown failed: {exception}");
            }

            try
            {
                if (host is not null) await host.DisposeAsync();
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Desktop host shutdown failed: {exception}");
            }

            appearance?.Dispose();
            text?.Dispose();
        }
        finally
        {
            _storageGate.Release();
        }
    }

    private Task ShowErrorAsync(string title, string message)
    {
        return new MessageDialog(title, message, Text[LanguageManager.Keys.OK]).ShowDialog(_window);
    }

    private string StorageSeparation(string firstKey, string secondKey)
    {
        return Text.Format(LanguageManager.Keys.StoragePathsMustBeSeparate, Text[firstKey], Text[secondKey]);
    }

    private string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(Text[LanguageManager.Keys.StoragePathRequired]);
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            throw new InvalidOperationException(Text[LanguageManager.Keys.StoragePathInvalid], exception);
        }
    }

    private static void VerifyWritable(string directory)
    {
        var probe = Path.Combine(directory, $".lemonneo-write-{Guid.NewGuid():N}.tmp");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.DeleteOnClose);
    }

    private string ValidateCacheDirectory(string directory, string? libraryDirectory = null)
    {
        var cache = NormalizeDirectory(directory);
        if (libraryDirectory is not null)
            EnsureSeparate(cache, libraryDirectory, StorageSeparation("CachePath", "LibraryPath"));
        EnsureSeparate(cache, _paths.ConfigurationDirectory, StorageSeparation("CachePath", "ConfigurationPath"));
        EnsureSeparate(cache, _paths.PluginDirectory, StorageSeparation("CachePath", "PluginPath"));
        EnsureSeparate(cache, _paths.DataProtectionKeysDirectory,
            StorageSeparation("CachePath", "DataProtectionKeysPath"));
        EnsureSeparate(cache, Path.Combine(_paths.DataDirectory, "Credentials"),
            StorageSeparation("CachePath", "CredentialsPath"));
        Directory.CreateDirectory(cache);
        VerifyWritable(cache);
        return cache;
    }

    private static void EnsureSeparate(string first, string second, string message)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var firstPrefix = first + Path.DirectorySeparatorChar;
        var secondNormalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        var secondPrefix = secondNormalized + Path.DirectorySeparatorChar;
        if (first.Equals(secondNormalized, comparison) || first.StartsWith(secondPrefix, comparison) ||
            secondNormalized.StartsWith(firstPrefix, comparison))
            throw new InvalidOperationException(message);
    }

    private static bool PathEquals(string first, string second)
    {
        return string.Equals(first, second, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private async Task SaveStoragePreferencesAsync(DesktopHost host, CancellationToken cancellationToken)
    {
        _settings.Apply<DesktopStoragePreferences>(_ => new DesktopStoragePreferences(
            host.StorageLocations.LibraryDirectory,
            host.StorageLocations.CacheDirectory));
        await host.Settings.SaveAsync(cancellationToken);
    }

    private sealed record ValidatedStoragePaths(string LibraryDirectory, string CacheDirectory);
}