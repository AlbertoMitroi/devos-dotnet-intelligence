using DotnetIntelligence.Artifacts;

namespace DotnetIntelligence.Cli;

/// <summary>verify &lt;generation&gt; (3.6, 5.5): schema, integrity and checksums; exit 0 or 50.</summary>
public static class VerifyCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 1)
        {
            throw new UsageException("Usage: verify <generation directory>");
        }
        var root = Path.GetFullPath(args[0]);
        var errors = GenerationVerifier.Verify(root);
        foreach (var note in errors.Count == 0 ? GenerationVerifier.UnknownKinds(root) : [])
        {
            await stdout.WriteLineAsync("note: " + note).ConfigureAwait(false);
        }
        if (errors.Count == 0)
        {
            await stdout.WriteLineAsync("ok: schema, integrity and checksums verified").ConfigureAwait(false);
            return ExitCodes.Complete;
        }
        foreach (var error in errors)
        {
            await stderr.WriteLineAsync(error).ConfigureAwait(false);
        }
        await stdout.WriteLineAsync($"failed: {errors.Count}{(errors.Count >= GenerationVerifier.MaxReportedErrors ? "+" : string.Empty)} problems").ConfigureAwait(false);
        return ExitCodes.IntegrityFailed;
    }
}
