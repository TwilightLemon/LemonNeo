using System.Text.Json;
using LemonNeo.Configuration;

namespace LemonNeo.ConsoleApp;

internal sealed class CommandHistory(IAppPathProvider paths)
{
    private const int MaximumEntries = 1000;
    private readonly List<string> _entries = [];

    public string FilePath { get; } = Path.Combine(paths.DataDirectory, "command-history.json");
    public IReadOnlyList<string> Entries => _entries;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            await using var stream = File.OpenRead(FilePath);
            var entries = await JsonSerializer.DeserializeAsync<string[]>(stream,
                cancellationToken: cancellationToken);
            _entries.Clear();
            foreach (var entry in entries ?? [])
            {
                AddToMemory(entry);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            global::System.Console.WriteLine($"History load failed: {exception.Message}");
        }
    }

    public async Task RecordAsync(string line, CancellationToken cancellationToken)
    {
        if (!AddToMemory(line))
        {
            return;
        }

        var temporaryPath = FilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, _entries,
                    cancellationToken: cancellationToken);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            global::System.Console.WriteLine($"History save failed: {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // History persistence must not prevent command execution.
            }
        }
    }

    private bool AddToMemory(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#') ||
            line.Contains('\r') || line.Contains('\n'))
        {
            return false;
        }

        IReadOnlyList<string> arguments;
        try
        {
            arguments = CommandProcessor.Tokenize(line);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (arguments.Count == 0 ||
            (string.Equals(arguments[0], "login", StringComparison.OrdinalIgnoreCase) && arguments.Count > 2) ||
            (_entries.Count > 0 && string.Equals(_entries[^1], line, StringComparison.Ordinal)))
        {
            return false;
        }

        _entries.Add(line);
        if (_entries.Count > MaximumEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaximumEntries);
        }

        return true;
    }
}
