using System.Collections.Concurrent;
using System.Text;
using DotnetIntelligence.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotnetIntelligence.Analysis.Extraction;

/// <summary>
/// Maps Roslyn locations to evidence locations. Committed files keep their repository path and blob id.
/// Text that exists only in the build (source generators, MSBuild-generated files under obj/) is stored in the
/// generation under generated/ with a computed blob id, the work directory replaced by "/_/" so the bytes do not
/// depend on the host. Locations inside generated code that map back to a committed file through #line (Razor)
/// are reported against that file.
/// </summary>
public sealed class FileMap
{
    private const string GeneratedPrefix = "generated/";
    private readonly string _root;
    private readonly string _objectFormat;
    private readonly IReadOnlyDictionary<string, string> _blobs;
    private readonly ConcurrentDictionary<SyntaxTree, TreeInfo> _trees = new();
    private readonly GraphBuilder _graph;

    public FileMap(string workRoot, string objectFormat, IEnumerable<WorkingCopyFile> files, GraphBuilder graph)
    {
        _root = Path.GetFullPath(workRoot).TrimEnd('/') + "/";
        _objectFormat = objectFormat;
        _blobs = files.ToDictionary(f => f.Path, f => f.BlobId, StringComparer.Ordinal);
        _graph = graph;
    }

    public string Root => _root;

    public string Relative(string path) =>
        path.StartsWith(_root, StringComparison.Ordinal) ? path[_root.Length..] : path;

    public bool IsCommitted(string relativePath) => _blobs.ContainsKey(relativePath);

    public string? BlobOf(string relativePath) => _blobs.GetValueOrDefault(relativePath);

    /// <summary>Registers the source generator that produced a tree (from the project's generated documents).</summary>
    public void RegisterGenerated(SyntaxTree tree, string generator) =>
        _trees.TryAdd(tree, CreateGenerated(tree, generator));

    /// <summary>Evidence location of a Roslyn location; null when it is not in source.</summary>
    public SourceLocation? Locate(Location location)
    {
        if (!location.IsInSource || location.SourceTree is null)
        {
            return null;
        }
        var info = Info(location.SourceTree);
        if (info.Store == "generation")
        {
            var mapped = location.GetMappedLineSpan();
            if (mapped.HasMappedPath)
            {
                var relative = Relative(Path.GetFullPath(mapped.Path));
                if (_blobs.TryGetValue(relative, out var mappedBlob))
                {
                    return new SourceLocation(relative, mappedBlob, ToSpan(mapped.Span), "repository", info.Generator);
                }
            }
        }
        return new SourceLocation(info.Path, info.BlobId, ToSpan(location.GetLineSpan().Span), info.Store, info.Generator);
    }

    /// <summary>The text of the line where a location starts, for snippets; mapped Razor lines are read from the committed file.</summary>
    public string? LineText(Location location, SourceLocation resolved)
    {
        if (resolved.Store == "repository" && location.SourceTree is { } tree && Info(tree).Store == "generation")
        {
            var file = Path.Combine(_root, resolved.Path);
            if (!File.Exists(file))
            {
                return null;
            }
            var lines = File.ReadLines(file).Skip(resolved.Span.StartLine - 1).Take(1).ToList();
            return lines.Count == 1 ? lines[0] : null;
        }
        var text = location.SourceTree!.GetText();
        var line = text.Lines.GetLineFromPosition(location.SourceSpan.Start);
        // Generated text is stored with the work directory replaced; snippets follow the stored bytes.
        return text.ToString(line.Span).Replace(_root, "/_/", StringComparison.Ordinal);
    }

    public static SourceSpan ToSpan(LinePositionSpan span) =>
        new(span.Start.Line + 1, span.Start.Character + 1, span.End.Line + 1, span.End.Character + 1);

    private TreeInfo Info(SyntaxTree tree) => _trees.GetOrAdd(tree, t =>
    {
        var relative = Relative(Path.GetFullPath(t.FilePath));
        if (_blobs.TryGetValue(relative, out var blob))
        {
            return new TreeInfo(relative, blob, "repository", null);
        }
        // Not committed and not reported as a source generator output: written by MSBuild (obj/), for example GlobalUsings.g.cs.
        return CreateGenerated(t, "msbuild");
    });

    private TreeInfo CreateGenerated(SyntaxTree tree, string generator)
    {
        var relative = Relative(Path.GetFullPath(tree.FilePath)).Replace('\\', '/');
        var safeGenerator = generator.Replace('/', '_');
        var path = GeneratedPrefix + safeGenerator + "/" + relative.TrimStart('/');
        var content = Encoding.UTF8.GetBytes(tree.GetText().ToString().Replace(_root, "/_/", StringComparison.Ordinal));
        _graph.AddGeneratedFile(path, content);
        return new TreeInfo(path, WorkingCopyExporter.ComputeBlobId(content, _objectFormat), "generation", generator);
    }

    private sealed record TreeInfo(string Path, string BlobId, string Store, string? Generator);
}
