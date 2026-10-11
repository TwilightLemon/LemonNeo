namespace LemonNeo.Abstractions.Settings;

public interface ISettingsEntityStore
{
    Task<T?> LoadAsync<T>(string packageId, string entityName, CancellationToken cancellationToken = default)
        where T : class;

    Task SaveAsync<T>(string packageId, string entityName, T value, CancellationToken cancellationToken = default)
        where T : class;
}