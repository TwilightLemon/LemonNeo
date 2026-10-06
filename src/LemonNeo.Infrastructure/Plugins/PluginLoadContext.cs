using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace LemonNeo.Infrastructure.Plugins;

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
            var fileName = GetNativeLibraryFileName(unmanagedDllName);
            var candidates = new List<string>
            {
                Path.Combine(_pluginDirectory, fileName)
            };
            var runtimeIdentifier = GetCurrentRuntimeIdentifier();
            if (runtimeIdentifier is not null)
            {
                candidates.Add(Path.Combine(
                    _pluginDirectory,
                    "runtimes",
                    runtimeIdentifier,
                    "native",
                    fileName));
            }

            path = candidates.FirstOrDefault(File.Exists);
        }

        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static string GetNativeLibraryFileName(string unmanagedDllName)
    {
        if (Path.HasExtension(unmanagedDllName))
        {
            return unmanagedDllName;
        }

        if (OperatingSystem.IsWindows())
        {
            return unmanagedDllName + ".dll";
        }

        if (OperatingSystem.IsLinux())
        {
            return "lib" + unmanagedDllName + ".so";
        }

        return OperatingSystem.IsMacOS()
            ? "lib" + unmanagedDllName + ".dylib"
            : unmanagedDllName;
    }

    private static string? GetCurrentRuntimeIdentifier()
    {
        var operatingSystem = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "osx"
                    : null;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => null
        };
        return operatingSystem is null || architecture is null
            ? null
            : $"{operatingSystem}-{architecture}";
    }

    private static bool IsSharedContract(string? assemblyName) =>
        assemblyName is "LemonNeo.Core" or "LemonNeo.Abstractions" ||
        assemblyName?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true;
}
