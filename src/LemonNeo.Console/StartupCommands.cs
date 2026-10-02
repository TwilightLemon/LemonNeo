namespace LemonNeo.ConsoleApp;

internal sealed class StartupCommands(string path)
{
    public const string DefaultScript = "# Show version\nversion\n# Show loaded plugins\nplugins\n# Show library statistics\nlibrary\n";

    public string FilePath { get; } = Path.GetFullPath(path);

    public async Task<string[]> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            await WriteAsync(DefaultScript, cancellationToken);
        }

        return await File.ReadAllLinesAsync(FilePath, cancellationToken);
    }

    public async Task WriteAsync(string script, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, script, cancellationToken);
        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}
