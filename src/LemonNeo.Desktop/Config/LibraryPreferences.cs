namespace LemonNeo.Desktop.Config;

public sealed record LibraryPreferences
{
    public bool InsertAtStart { get; init; }
    public Guid[] PlaylistOrder { get; init; } = [];
}