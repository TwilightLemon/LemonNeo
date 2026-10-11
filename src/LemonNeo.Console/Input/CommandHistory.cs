using System.Collections.Immutable;
using System.Text.Json;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;

namespace LemonNeo.ConsoleApp;

internal sealed record CommandHistoryState(ImmutableArray<string> Entries = default);

internal sealed class CommandHistory(IAppPathProvider paths, IAppSettingsService settings)
{
    private const int MaximumEntries = 1000;
    private readonly List<string> _entries = [];
    private readonly ISettingsScope _settings = settings.ForPackage(PackageNames.Console);
    private bool _available;

    public string FilePath { get; } =
        paths.GetEntityConfigurationFile(PackageNames.Console, nameof(CommandHistoryState));

    public IReadOnlyList<string> Entries => _entries;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _settings.RegisterAsync(nameof(CommandHistoryState), new CommandHistoryState([]),
                value => value.Entries.IsDefault ? new CommandHistoryState([]) : value, cancellationToken);
            _entries.Clear();
            foreach (var entry in _settings.Get<CommandHistoryState>().Entries) AddToMemory(entry);
            _available = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"History load failed: {exception.Message}");
        }
    }

    public Task RecordAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AddToMemory(line) && _available)
            _settings.Apply<CommandHistoryState>(_ => new CommandHistoryState(_entries.ToImmutableArray()));
        return Task.CompletedTask;
    }

    private bool AddToMemory(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#') ||
            line.Contains('\r') || line.Contains('\n')) return false;
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
            (_entries.Count > 0 && string.Equals(_entries[^1], line, StringComparison.Ordinal))) return false;
        _entries.Add(line);
        if (_entries.Count > MaximumEntries) _entries.RemoveRange(0, _entries.Count - MaximumEntries);
        return true;
    }
}