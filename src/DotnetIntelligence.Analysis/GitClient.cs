using System.Diagnostics;
using System.Text;

namespace DotnetIntelligence.Analysis;

public sealed record GitTreeEntry(string Mode, string Type, string ObjectId, string Path);

public sealed class GitException(string message) : Exception(message);

/// <summary>
/// Git calls made by the engine. Every call registers the repository as a safe directory through
/// "-c safe.directory=..." for that call only, so the caller's Git configuration is never changed (2.3).
/// </summary>
public sealed class GitClient(string repositoryPath)
{
    private static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["LC_ALL"] = "C",
    };

    public string RepositoryPath { get; } = Path.GetFullPath(repositoryPath);

    private IEnumerable<string> BaseArguments(IEnumerable<string> arguments) =>
        new[] { "-c", "safe.directory=" + RepositoryPath, "-c", "core.quotePath=false", "-C", RepositoryPath }.Concat(arguments);

    public async Task<ProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        await ProcessRunner.RunAsync("git", BaseArguments(arguments), RepositoryPath, Environment, cancellationToken).ConfigureAwait(false);

    public async Task<string> RunCheckedAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var args = arguments.ToArray();
        var result = await RunAsync(args, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new GitException($"git {args.FirstOrDefault()} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
        return result.StandardOutputText;
    }

    public async Task<bool> IsRepositoryAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["rev-parse", "--is-inside-work-tree"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && result.StandardOutputText.Trim() == "true";
    }

    public async Task<string> HeadCommitAsync(CancellationToken cancellationToken = default) =>
        (await RunCheckedAsync(["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken).ConfigureAwait(false)).Trim();

    public async Task<string> ObjectFormatAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["rev-parse", "--show-object-format"], cancellationToken).ConfigureAwait(false);
        var format = result.StandardOutputText.Trim();
        return result.ExitCode == 0 && format.Length > 0 ? format : "sha1";
    }

    public async Task<bool> IsShallowAsync(CancellationToken cancellationToken = default) =>
        (await RunCheckedAsync(["rev-parse", "--is-shallow-repository"], cancellationToken).ConfigureAwait(false)).Trim() == "true";

    /// <summary>The fetch URL of "origin", or null when there is none.</summary>
    public async Task<string?> OriginUrlAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["config", "--get", "remote.origin.url"], cancellationToken).ConfigureAwait(false);
        var url = result.StandardOutputText.Trim();
        return result.ExitCode == 0 && url.Length > 0 ? url : null;
    }

    public async Task<IReadOnlyList<GitTreeEntry>> ListTreeAsync(string commit, CancellationToken cancellationToken = default)
    {
        var output = await RunCheckedAsync(["ls-tree", "-r", "-z", "--full-tree", commit], cancellationToken).ConfigureAwait(false);
        var entries = new List<GitTreeEntry>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> SP <type> SP <object> TAB <path>"
            var tab = record.IndexOf('\t');
            var header = record[..tab].Split(' ');
            entries.Add(new GitTreeEntry(header[0], header[1], header[2], record[(tab + 1)..]));
        }
        return entries;
    }

    /// <summary>Paths of tracked and untracked (not ignored) files in the working tree.</summary>
    public async Task<IReadOnlyList<string>> ListWorkingTreeFilesAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunCheckedAsync(["ls-files", "-z", "--cached", "--others", "--exclude-standard"], cancellationToken).ConfigureAwait(false);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads blobs through one "git cat-file --batch" process; the bytes are exactly the committed ones.</summary>
    public async Task ReadBlobsAsync(
        IEnumerable<string> objectIds,
        Func<string, byte[], Task> onBlob,
        CancellationToken cancellationToken = default)
    {
        var startInfo = ProcessRunner.CreateStartInfo("git", BaseArguments(["cat-file", "--batch"]), RepositoryPath, Environment);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var input = process.StandardInput.BaseStream;
        var output = process.StandardOutput.BaseStream;
        try
        {
            foreach (var objectId in objectIds)
            {
                await input.WriteAsync(Encoding.ASCII.GetBytes(objectId + "\n"), cancellationToken).ConfigureAwait(false);
                await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                var header = await ReadLineAsync(output, cancellationToken).ConfigureAwait(false);
                var parts = header.Split(' ');
                if (parts.Length != 3 || parts[1] != "blob")
                {
                    throw new GitException($"Unexpected cat-file response for {objectId}: '{header}'.");
                }
                var size = long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                var content = new byte[size];
                await output.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
                var terminator = new byte[1];
                await output.ReadExactlyAsync(terminator, cancellationToken).ConfigureAwait(false);
                await onBlob(objectId, content).ConfigureAwait(false);
            }
            input.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.TryKill(process);
            throw;
        }
        if (process.ExitCode != 0)
        {
            throw new GitException($"git cat-file failed with exit code {process.ExitCode}: {(await stderrTask.ConfigureAwait(false)).Trim()}");
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new GitException("git cat-file ended unexpectedly.");
            }
            if (one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.Add(one[0]);
        }
    }
}
