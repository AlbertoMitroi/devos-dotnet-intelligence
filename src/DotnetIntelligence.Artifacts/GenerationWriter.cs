using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Artifacts;

public sealed record GenerationFile(string Path, string Sha256, long Size);

/// <summary>
/// Writes a generation directory. Artifact files first, then manifest.json (listing every file with its
/// SHA-256), then run.json (5.6). Without a manifest, a directory is never a generation.
/// </summary>
public sealed class GenerationWriter
{
    public const string ManifestFileName = "manifest.json";
    public const string RunFileName = "run.json";

    private readonly string _root;
    private readonly SortedSet<string> _files = new(StringComparer.Ordinal);
    private bool _finalized;

    public GenerationWriter(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        if (Directory.EnumerateFileSystemEntries(_root).Any())
        {
            throw new IOException($"Output directory '{root}' is not empty.");
        }
    }

    public string Root => _root;

    /// <summary>Writes NDJSON records sorted by their "id" field (ordinal).</summary>
    public void WriteNdjson(string relativePath, IEnumerable<JsonObject> records)
    {
        var sorted = records
            .OrderBy(r => (string?)r["id"] ?? throw new InvalidOperationException($"Record without id in {relativePath}."), StringComparer.Ordinal)
            .ToList();
        var duplicates = sorted.GroupBy(r => (string)r["id"]!).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"Duplicate ids in {relativePath}: {string.Join(", ", duplicates)}.");
        }
        using var stream = Create(relativePath);
        CanonicalJson.WriteNdjson(stream, sorted);
    }

    public void WriteBytes(string relativePath, byte[] content)
    {
        using var stream = Create(relativePath);
        stream.Write(content);
    }

    /// <summary>Registers a file that another writer (SQLite) creates at the returned absolute path.</summary>
    public string Reserve(string relativePath)
    {
        Create(relativePath).Dispose();
        var absolute = Path.Combine(_root, relativePath);
        File.Delete(absolute);
        return absolute;
    }

    public void WriteJson(string relativePath, JsonNode document)
    {
        using var stream = Create(relativePath);
        stream.Write(CanonicalJson.SerializeToUtf8(document));
        stream.WriteByte((byte)'\n');
    }

    /// <summary>Writes manifest.json last; returns the file list it contains.</summary>
    public IReadOnlyList<GenerationFile> WriteManifest(JsonObject manifest)
    {
        EnsureNotFinalized();
        var files = _files.Select(path =>
        {
            var absolute = Path.Combine(_root, path);
            return new GenerationFile(path, Hashing.Sha256HexOfFile(absolute), new FileInfo(absolute).Length);
        }).ToList();
        manifest["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Path,
            ["sha256"] = f.Sha256,
            ["size"] = f.Size,
        }).ToArray());
        CanonicalJson.WriteDocument(Path.Combine(_root, ManifestFileName), manifest);
        _finalized = true;
        return files;
    }

    /// <summary>run.json: timings and host data, excluded from determinism checks; written after the manifest.</summary>
    public void WriteRun(JsonObject run)
    {
        if (!_finalized)
        {
            throw new InvalidOperationException("run.json is written after the manifest.");
        }
        CanonicalJson.WriteDocument(Path.Combine(_root, RunFileName), run);
    }

    private FileStream Create(string relativePath)
    {
        EnsureNotFinalized();
        var normalized = relativePath.Replace('\\', '/');
        if (normalized is ManifestFileName or RunFileName || normalized.StartsWith('/') || normalized.Contains(".."))
        {
            throw new ArgumentException($"Invalid artifact path '{relativePath}'.", nameof(relativePath));
        }
        if (!_files.Add(normalized))
        {
            throw new InvalidOperationException($"Artifact '{normalized}' written twice.");
        }
        var absolute = Path.Combine(_root, normalized);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        return File.Create(absolute);
    }

    private void EnsureNotFinalized()
    {
        if (_finalized)
        {
            throw new InvalidOperationException("The generation is already finalized.");
        }
    }
}
