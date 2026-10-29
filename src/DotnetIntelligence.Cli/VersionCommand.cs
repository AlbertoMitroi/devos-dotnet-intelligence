using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Cli;

public static class VersionCommand
{
    public static async Task<JsonObject> DescribeAsync(IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var inventory = await SdkInventory.ReadAsync(cancellationToken).ConfigureAwait(false);
        var roslyn = SdkInventory.EngineRoslyn();
        return new JsonObject
        {
            ["engine"] = new JsonObject { ["name"] = EngineInfo.Name, ["version"] = EngineInfo.Version },
            ["schemaVersion"] = EngineInfo.SchemaVersion,
            ["roslyn"] = RoslynJson(roslyn),
            ["roslynCompatible"] = SdkInventory.IsRoslynCompatible(roslyn.AssemblyVersion, inventory.Sdks),
            ["runtime"] = new JsonObject
            {
                ["framework"] = RuntimeInformation.FrameworkDescription,
                ["os"] = EngineInfo.OperatingSystem,
                ["architecture"] = EngineInfo.Architecture,
            },
            ["sdks"] = SdksJson(inventory.Sdks),
            ["runtimes"] = new JsonArray(inventory.Runtimes.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name,
                ["version"] = r.Version,
            }).ToArray()),
            ["imageDigest"] = EngineInfo.ImageDigest(environment),
        };
    }

    public static JsonObject RoslynJson(RoslynVersion roslyn) => new()
    {
        ["apiVersion"] = roslyn.ApiVersion,
        ["build"] = roslyn.Build,
    };

    public static JsonArray SdksJson(IEnumerable<InstalledSdk> sdks) => new(sdks.Select(s => (JsonNode)new JsonObject
    {
        ["version"] = s.Version,
        ["compilerBuild"] = s.CompilerVersion ?? "unknown",
        ["compilerApiVersion"] = s.CompilerAssemblyVersion?.ToString(3) ?? "unknown",
    }).ToArray());

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var json = args.Contains("--json");
        if (args.Any(a => a != "--json"))
        {
            throw new UsageException("Usage: version [--json]");
        }
        if (!json)
        {
            await stdout.WriteLineAsync($"{EngineInfo.Name} {EngineInfo.Version}").ConfigureAwait(false);
            return ExitCodes.Complete;
        }
        var description = await DescribeAsync(environment, cancellationToken).ConfigureAwait(false);
        await stdout.WriteAsync(CanonicalJson.Serialize(description) + "\n").ConfigureAwait(false);
        return ExitCodes.Complete;
    }
}
