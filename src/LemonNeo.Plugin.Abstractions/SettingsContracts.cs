namespace LemonNeo.Plugin.Abstractions;

/// <summary>Configuration access bound to one package. Apply changes memory, not files.</summary>
public interface ISettingsScope
{
    string PackageId { get; }
    void Register<T>(string entityName, T defaults, Func<T, T>? normalize = null) where T : class;
    Task RegisterAsync<T>(string entityName, T defaults, Func<T, T>? normalize = null,
        CancellationToken cancellationToken = default) where T : class;
    T Get<T>() where T : class;
    void Apply<T>(Func<T, T> update) where T : class;
    IDisposable Subscribe<T>(Action<T> onChanged) where T : class;
}

public interface IAppSettingsService
{
    ISettingsScope ForPackage(string packageId);
    Task LoadAsync(CancellationToken cancellationToken = default);
    void Start();
    Task StopAsync();
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task ReleasePackageAsync(string packageId, CancellationToken cancellationToken = default);
}

public static class PackageNames
{
    public const string Application = "LemonNeo.Application";
    public const string Desktop = "LemonNeo.Desktop";
    public const string Console = "LemonNeo.Console";

    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Split('.').All(IsIdentifier);

    public static bool IsIdentifier(string value) => value.Length > 0 &&
        value[0] is >= 'A' and <= 'Z' && value.All(char.IsAsciiLetterOrDigit);

    public static void Validate(string value)
    {
        if (!IsValid(value))
            throw new ArgumentException("Package names must use dotted PascalCase identifiers, such as LemonNeo.Plugin.Netease.", nameof(value));
    }
}
