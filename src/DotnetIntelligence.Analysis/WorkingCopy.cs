using System.Security.Cryptography;
using System.Text;

namespace DotnetIntelligence.Analysis;

public sealed record WorkingCopyFile(string Path, string BlobId);

public sealed record WorkingCopy(
    string Root,
    string Commit,
    string ObjectFormat,
    string? RemoteUrl,
    bool Dirty,
    bool Shallow,
    IReadOnlyList<WorkingCopyFile> Files,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics);

/// <summary>
/// Builds the engine's own copy of the repository under the work directory (2.3, D8).
/// Committed state: blobs read through "git cat-file --batch", so bytes and blob ids match the commit
/// on every host OS. With includeWorkingTree: files copied from disk, blob ids computed, dirty flag set.
/// </summary>
public static class WorkingCopyExporter
{
    private const string LfsPointerPrefix = "version https://git-lfs.github.com/spec/v1";

    public static async Task<WorkingCopy> ExportAsync(
        string sourcePath,
        string workRoot,
        bool includeWorkingTree,
        IReadOnlyCollection<string> excludedAbsolutePaths,
        CancellationToken cancellationToken = default)
    {
        var git = new GitClient(sourcePath);
        if (!await git.IsRepositoryAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new GitException($"'{sourcePath}' is not a Git working tree.");
        }

        var commit = await git.HeadCommitAsync(cancellationToken).ConfigureAwait(false);
        var objectFormat = await git.ObjectFormatAsync(cancellationToken).ConfigureAwait(false);
        var shallow = await git.IsShallowAsync(cancellationToken).ConfigureAwait(false);
        var origin = await git.OriginUrlAsync(cancellationToken).ConfigureAwait(false);
        var tree = await git.ListTreeAsync(commit, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(workRoot);
        if (Directory.EnumerateFileSystemEntries(workRoot).Any())
        {
            throw new IOException($"Work directory '{workRoot}' is not empty.");
        }

        var diagnostics = new List<AnalysisDiagnostic>();
        foreach (var entry in tree)
        {
            if (entry.Type == "commit")
            {
                var initialized = Directory.Exists(Path.Combine(git.RepositoryPath, entry.Path))
                    && Directory.EnumerateFileSystemEntries(Path.Combine(git.RepositoryPath, entry.Path)).Any();
                diagnostics.Add(initialized
                    ? new AnalysisDiagnostic(DiagnosticSeverity.Warning, "submodule-not-analyzed", entry.Path, DiagnosticState.Unsupported,
                        $"Submodule at '{entry.Path}' (commit {entry.ObjectId}) is not analyzed; submodules are outside the repository's own content.")
                    : new AnalysisDiagnostic(DiagnosticSeverity.Warning, "submodule-uninitialized", entry.Path, DiagnosticState.Unknown,
                        $"Submodule at '{entry.Path}' (commit {entry.ObjectId}) is not initialized; its content is unknown."));
            }
            else if (entry.Mode == "120000")
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "symlink-skipped", entry.Path, DiagnosticState.Unsupported,
                    $"Symbolic link '{entry.Path}' is not followed."));
            }
        }

        List<WorkingCopyFile> files;
        bool dirty;
        if (includeWorkingTree)
        {
            (files, dirty) = await CopyWorkingTreeAsync(git, tree, workRoot, objectFormat, excludedAbsolutePaths, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            files = await ExportCommittedAsync(git, tree, workRoot, diagnostics, cancellationToken).ConfigureAwait(false);
            dirty = false;
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new WorkingCopy(
            workRoot,
            commit,
            objectFormat,
            origin is null ? null : RemoteUrl.Redact(origin),
            dirty,
            shallow,
            files,
            diagnostics);
    }

    private static async Task<List<WorkingCopyFile>> ExportCommittedAsync(
        GitClient git,
        IReadOnlyList<GitTreeEntry> tree,
        string workRoot,
        List<AnalysisDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var blobs = tree.Where(e => e.Type == "blob" && e.Mode != "120000").ToList();
        var pathsByObject = blobs.GroupBy(e => e.ObjectId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Path).ToList(), StringComparer.Ordinal);
        var files = new List<WorkingCopyFile>();
        await git.ReadBlobsAsync(pathsByObject.Keys.Order(StringComparer.Ordinal), async (objectId, content) =>
        {
            foreach (var path in pathsByObject[objectId])
            {
                await WriteFileAsync(workRoot, path, content, cancellationToken).ConfigureAwait(false);
                files.Add(new WorkingCopyFile(path, objectId));
                ReportLfsPointer(path, content, diagnostics);
            }
        }, cancellationToken).ConfigureAwait(false);
        return files;
    }

    private static async Task<(List<WorkingCopyFile> Files, bool Dirty)> CopyWorkingTreeAsync(
        GitClient git,
        IReadOnlyList<GitTreeEntry> tree,
        string workRoot,
        string objectFormat,
        IReadOnlyCollection<string> excludedAbsolutePaths,
        List<AnalysisDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var committed = tree.Where(e => e.Type == "blob" && e.Mode != "120000").ToDictionary(e => e.Path, e => e.ObjectId, StringComparer.Ordinal);
        var excluded = excludedAbsolutePaths.Select(p => Path.GetFullPath(p).TrimEnd('/') + "/").ToList();
        var files = new List<WorkingCopyFile>();
        var dirty = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in await git.ListWorkingTreeFilesAsync(cancellationToken).ConfigureAwait(false))
        {
            var absolute = Path.Combine(git.RepositoryPath, path);
            if (excluded.Any(prefix => (absolute + "/").StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }
            var info = new FileInfo(absolute);
            if (!info.Exists || info.LinkTarget is not null)
            {
                // Deleted tracked file, a directory entry (submodule) or a symlink.
                if (committed.ContainsKey(path) && !info.Exists && !Directory.Exists(absolute))
                {
                    dirty = true;
                }
                continue;
            }
            var content = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
            var blobId = ComputeBlobId(content, objectFormat);
            seen.Add(path);
            if (!committed.TryGetValue(path, out var committedId) || committedId != blobId)
            {
                dirty = true;
            }
            await WriteFileAsync(workRoot, path, content, cancellationToken).ConfigureAwait(false);
            files.Add(new WorkingCopyFile(path, blobId));
            ReportLfsPointer(path, content, diagnostics);
        }
        if (committed.Keys.Any(p => !seen.Contains(p)))
        {
            dirty = true;
        }
        return (files, dirty);
    }

    /// <summary>Git blob id: hash of "blob {size}\0" followed by the content (SHA-1 or SHA-256 repositories).</summary>
    public static string ComputeBlobId(byte[] content, string objectFormat)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var data = new byte[header.Length + content.Length];
        header.CopyTo(data, 0);
        content.CopyTo(data, header.Length);
        return objectFormat == "sha256"
            ? Convert.ToHexStringLower(SHA256.HashData(data))
            : Convert.ToHexStringLower(SHA1.HashData(data));
    }

    private static async Task WriteFileAsync(string workRoot, string relativePath, byte[] content, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workRoot).TrimEnd('/') + "/";
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!target.StartsWith(root, StringComparison.Ordinal))
        {
            throw new IOException($"Path '{relativePath}' escapes the work directory.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, content, cancellationToken).ConfigureAwait(false);
    }

    private static void ReportLfsPointer(string path, byte[] content, List<AnalysisDiagnostic> diagnostics)
    {
        if (content.Length < 200 && Encoding.ASCII.GetString(content).StartsWith(LfsPointerPrefix, StringComparison.Ordinal))
        {
            diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "lfs-pointer", path, DiagnosticState.Unsupported,
                $"'{path}' is a Git LFS pointer; its content is not fetched."));
        }
    }
}
