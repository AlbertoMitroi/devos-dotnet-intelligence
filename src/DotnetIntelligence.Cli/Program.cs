using System.Collections;
using DotnetIntelligence.Cli;

return await CommandLine.RunAsync(args, Console.Out, Console.Error, CommandLine.ReadEnvironment(), CancellationToken.None);

namespace DotnetIntelligence.Cli
{
    public static class CommandLine
    {
        public const string Help =
            "dotnet-intelligence: context artifacts for a .NET repository at one commit.\n" +
            "Commands:\n" +
            "  version [--json]\n" +
            "  " + AnalyzeCommand.Usage + "\n" +
            "  verify <generation>\n" +
            "  query <generation> <symbol|refs|callers|callees|impl|di|routes|endpoints|http|calls-to|messages|trace|search|dump> [args] [--json]\n" +
            "  index <generation> <file.sqlite>   (rebuild the derived index apart from the generation)\n" +
            "  gha    (GitHub Docker container action mode; reads INPUT_* variables)\n";

        public static IReadOnlyDictionary<string, string?> ReadEnvironment()
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                result[(string)entry.Key] = entry.Value as string;
            }
            return result;
        }

        public static async Task<int> RunAsync(
            string[] args,
            TextWriter stdout,
            TextWriter stderr,
            IReadOnlyDictionary<string, string?> environment,
            CancellationToken cancellationToken)
        {
            var events = new EventWriter(stdout, stderr);
            try
            {
                return args.FirstOrDefault() switch
                {
                    "version" => await VersionCommand.RunAsync(args[1..], stdout, environment, cancellationToken).ConfigureAwait(false),
                    "analyze" => await AnalyzeCommand.RunAsync(args[1..], events, environment, cancellationToken).ConfigureAwait(false),
                    "gha" => await GitHubActionMode.RunAsync(events, stdout, environment, cancellationToken).ConfigureAwait(false),
                    "verify" => await VerifyCommand.RunAsync(args[1..], stdout, stderr).ConfigureAwait(false),
                    "query" => await QueryCommand.RunAsync(args[1..], stdout).ConfigureAwait(false),
                    "index" => await QueryCommand.IndexAsync(args[1..], stdout).ConfigureAwait(false),
                    "help" or "--help" or "-h" => await WriteHelpAsync(stdout).ConfigureAwait(false),
                    _ => throw new UsageException(Help),
                };
            }
            catch (UsageException ex)
            {
                await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCodes.Usage;
            }
            catch (IOException ex)
            {
                await stderr.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
                return ExitCodes.InternalError;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await stderr.WriteLineAsync($"internal error: {ex}").ConfigureAwait(false);
                return ExitCodes.InternalError;
            }
        }

        private static async Task<int> WriteHelpAsync(TextWriter stdout)
        {
            await stdout.WriteAsync(Help).ConfigureAwait(false);
            return ExitCodes.Complete;
        }
    }
}
