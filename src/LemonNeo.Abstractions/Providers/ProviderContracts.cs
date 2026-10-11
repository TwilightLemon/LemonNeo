namespace LemonNeo.Abstractions.Providers;

public sealed record ProviderDescriptor(string Name, string Description);

public interface IMusicProvider
{
    string Id { get; }
    ProviderDescriptor Descriptor { get; }
}

public interface IProviderRegistry
{
    IReadOnlyCollection<IMusicProvider> Providers { get; }
    IMusicProvider? Get(string providerId);
}