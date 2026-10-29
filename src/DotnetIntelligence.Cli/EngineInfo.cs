using System.Reflection;
using System.Runtime.InteropServices;

namespace DotnetIntelligence.Cli;

public static class EngineInfo
{
    public const string Name = "dotnet-intelligence";

    /// <summary>Contract version written into manifests (schema/v1, docs/CONTRACT.md).</summary>
    public const string SchemaVersion = "1.0";

    public static string Version { get; } =
        (typeof(EngineInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+', 2)[0];

    /// <summary>The image digest is not knowable inside its own image; callers pass it through DI_IMAGE_DIGEST.</summary>
    public static string ImageDigest(IReadOnlyDictionary<string, string?> environment) =>
        environment.TryGetValue("DI_IMAGE_DIGEST", out var digest) && !string.IsNullOrWhiteSpace(digest) ? digest : "unknown";

    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static string OperatingSystem =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : "unknown";
}
