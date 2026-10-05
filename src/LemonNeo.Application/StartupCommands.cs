using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed record StartupPreferences(string Script = StartupCommands.DefaultScript);

/// <summary>Shared startup commands stored as an Application entity; --startup selects an explicit script file.</summary>
public sealed class StartupCommands(IAppSettingsService settings, string configurationFile, string? scriptPath = null)
{
    public const string DefaultScript = "# Show version\nversion\n# Show loaded plugins\nplugins\n# Show library statistics\nlibrary\n";
    private readonly ISettingsScope _settings = settings.ForPackage(PackageNames.Application);
    public string FilePath { get; } = Path.GetFullPath(scriptPath ?? configurationFile);

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _settings.RegisterAsync(nameof(StartupPreferences), new StartupPreferences(),
            cancellationToken: cancellationToken);

    public async Task<string[]> LoadAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (scriptPath is not null) return await File.ReadAllLinesAsync(FilePath, cancellationToken);
        return _settings.Get<StartupPreferences>().Script.Replace("\r\n", "\n").Split('\n');
    }

    public async Task WriteAsync(string script, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (scriptPath is null)
        {
            _settings.Apply<StartupPreferences>(current => current with { Script = script });
            return;
        }
        // An explicitly selected external script keeps its ordinary file-edit contract.
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, script, cancellationToken);
        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}
