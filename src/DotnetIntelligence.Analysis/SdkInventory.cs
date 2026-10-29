using System.Reflection;
using System.Text.RegularExpressions;

namespace DotnetIntelligence.Analysis;

/// <param name="CompilerVersion">Build of the SDK's compiler as printed by "csc -version", for example 5.9.0-1.26523.112.</param>
/// <param name="CompilerAssemblyVersion">Assembly version of the SDK's Microsoft.CodeAnalysis.dll, the version analyzers bind to.</param>
public sealed record InstalledSdk(string Version, string? CompilerVersion, Version? CompilerAssemblyVersion);

/// <param name="AssemblyVersion">Assembly version of the engine's Microsoft.CodeAnalysis.dll, for example 5.9.0.</param>
/// <param name="Build">Informational version of that assembly, for example 5.9.0-1.26357.3.</param>
public sealed record RoslynVersion(Version AssemblyVersion, string Build)
{
    public string ApiVersion => AssemblyVersion.ToString(3);
}

public sealed record InstalledRuntime(string Name, string Version);

public sealed record SdkInventoryResult(IReadOnlyList<InstalledSdk> Sdks, IReadOnlyList<InstalledRuntime> Runtimes);

/// <summary>
/// The SDK set of the image (2.2): SDKs, runtimes and the compiler version inside each SDK.
/// Treated as a list from the start so adding SDK 9 is an image change only (D15).
/// </summary>
public static partial class SdkInventory
{
    public static async Task<SdkInventoryResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var sdkOutput = await ProcessRunner.RunAsync("dotnet", ["--list-sdks"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var runtimeOutput = await ProcessRunner.RunAsync("dotnet", ["--list-runtimes"], cancellationToken: cancellationToken).ConfigureAwait(false);

        var sdks = new List<InstalledSdk>();
        foreach (Match match in ListLine().Matches(sdkOutput.StandardOutputText))
        {
            var version = match.Groups["version"].Value;
            var sdkDirectory = Path.Combine(match.Groups["root"].Value, version);
            sdks.Add(new InstalledSdk(
                version,
                await ReadCompilerVersionAsync(sdkDirectory, cancellationToken).ConfigureAwait(false),
                ReadCompilerAssemblyVersion(sdkDirectory)));
        }

        var runtimes = RuntimeLine().Matches(runtimeOutput.StandardOutputText)
            .Select(m => new InstalledRuntime(m.Groups["name"].Value, m.Groups["version"].Value))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Version, StringComparer.Ordinal)
            .ToList();

        return new SdkInventoryResult(sdks.OrderBy(s => s.Version, StringComparer.Ordinal).ToList(), runtimes);
    }

    /// <summary>Runs the SDK's own compiler with "-version"; null when the compiler cannot be found or run.</summary>
    public static async Task<string?> ReadCompilerVersionAsync(string sdkDirectory, CancellationToken cancellationToken = default)
    {
        var csc = Path.Combine(sdkDirectory, "Roslyn", "bincore", "csc.dll");
        if (!File.Exists(csc))
        {
            return null;
        }
        var result = await ProcessRunner.RunAsync("dotnet", [csc, "-version"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }
        // "5.9.0-1.26523.112 (5a2162be...)": keep the version, drop the commit.
        return result.StandardOutputText.Trim().Split(' ', 2)[0];
    }

    /// <summary>Assembly version of the SDK's Microsoft.CodeAnalysis.dll; null when it cannot be read.</summary>
    public static Version? ReadCompilerAssemblyVersion(string sdkDirectory)
    {
        var path = Path.Combine(sdkDirectory, "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll");
        try
        {
            return File.Exists(path) ? AssemblyName.GetAssemblyName(path).Version : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>The Microsoft.CodeAnalysis the engine is built with.</summary>
    public static RoslynVersion EngineRoslyn()
    {
        var assembly = typeof(Microsoft.CodeAnalysis.Compilation).Assembly;
        var version = assembly.GetName().Version ?? throw new InvalidOperationException("Roslyn assembly version is not available.");
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version.ToString(3);
        return new RoslynVersion(version, informational.Split('+', 2)[0]);
    }

    /// <summary>
    /// Rule 2.2 (D3): the engine's Roslyn assembly version is at least the compiler assembly version of every SDK.
    /// Assembly versions are what analyzers and source generators bind to; build numbers inside one release line
    /// (for example 5.9.0-1.26357.3 vs 5.9.0-1.26523.112) share an assembly version. Unknown compilers fail the rule.
    /// </summary>
    public static bool IsRoslynCompatible(Version engineAssemblyVersion, IEnumerable<InstalledSdk> sdks) =>
        sdks.All(s => s.CompilerAssemblyVersion is not null && engineAssemblyVersion >= s.CompilerAssemblyVersion);

    [GeneratedRegex(@"^(?<version>\S+) \[(?<root>[^\]]+)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex ListLine();

    [GeneratedRegex(@"^(?<name>\S+) (?<version>\S+) \[[^\]]+\]\s*$", RegexOptions.Multiline)]
    private static partial Regex RuntimeLine();
}
