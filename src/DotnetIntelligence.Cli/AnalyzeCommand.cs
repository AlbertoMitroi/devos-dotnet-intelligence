namespace DotnetIntelligence.Cli;

public static class AnalyzeCommand
{
    public const string Usage =
        "Usage: analyze [--src <dir>] [--out <dir>] [--config <file>] [--solution <path>] [--include-working-tree] [--offline]";

    public static AnalyzeOptions Parse(string[] args, IReadOnlyDictionary<string, string?> environment)
    {
        string source = "/src", output = "/out";
        string? config = null, solution = null;
        bool includeWorkingTree = false, offline = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new UsageException($"Missing value for {args[i]}.\n{Usage}");
            switch (args[i])
            {
                case "--src": source = Value(); break;
                case "--out": output = Value(); break;
                case "--config": config = Value(); break;
                case "--solution": solution = Value(); break;
                case "--include-working-tree": includeWorkingTree = true; break;
                case "--offline": offline = true; break;
                default: throw new UsageException($"Unknown option '{args[i]}'.\n{Usage}");
            }
        }
        var workBase = environment.TryGetValue("DI_WORK", out var work) && !string.IsNullOrWhiteSpace(work) ? work : "/work";
        var nugetPackages = environment.TryGetValue("NUGET_PACKAGES", out var packages) && !string.IsNullOrWhiteSpace(packages) ? packages : "/cache/nuget";
        return new AnalyzeOptions(Path.GetFullPath(source), Path.GetFullPath(output), config, solution, includeWorkingTree, offline, workBase,
            nugetPackages, [Path.GetFullPath(output)]);
    }

    public static async Task<int> RunAsync(
        string[] args,
        EventWriter events,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var options = Parse(args, environment);
        var outcome = await AnalyzeRunner.RunAsync(options, events, environment, cancellationToken).ConfigureAwait(false);
        return outcome.ExitCode;
    }
}
