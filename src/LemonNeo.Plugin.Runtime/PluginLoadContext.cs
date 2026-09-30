using System.Reflection;
using System.Runtime.Loader;

namespace LemonNeo.Plugin.Runtime;

internal sealed class PluginLoadContext(string entryAssemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);
    private readonly string _pluginDirectory = Path.GetDirectoryName(entryAssemblyPath)!;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedContract(assemblyName.Name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null && assemblyName.Name is not null)
        {
            var localPath = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            path = File.Exists(localPath) ? localPath : null;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            var fileName = Path.HasExtension(unmanagedDllName)
                ? unmanagedDllName
                : unmanagedDllName + ".dll";
            var architecture = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            var candidates = new[]
            {
                Path.Combine(_pluginDirectory, fileName),
                Path.Combine(_pluginDirectory, "runtimes", architecture, "native", fileName)
            };
            path = candidates.FirstOrDefault(File.Exists);
        }

        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static bool IsSharedContract(string? assemblyName) =>
        assemblyName is "LemonNeo.Core" or "LemonNeo.Plugin.Abstractions" ||
        assemblyName?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true;
}
