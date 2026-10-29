using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis.Graph;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Where observations from non-C# files (BPMN, Terraform) belong: the deepest project whose directory contains the
/// file, else the repository scope (project ".", one analysis context of its own, D22).
/// </summary>
public sealed class FileScope(GraphBuilder graph, string projectPath, string contextId)
{
    public GraphBuilder Graph { get; } = graph;
    public string ProjectPath { get; } = projectPath;
    public string ContextId { get; } = contextId;

    public string Evidence(string path, string blobId, int line, int column, int length, string? lineText, string method) =>
        Graph.AddEvidence(new SourceLocation(path, blobId, new SourceSpan(line, column, line, column + length), "repository", null), method, ContextId, lineText);

    public void Observe(string kind, JsonObject fields, params string?[] evidence)
    {
        var ids = evidence.Where(e => e is not null).Select(e => e!).ToList();
        if (ids.Count == 0)
        {
            return;
        }
        fields["project"] = ProjectPath;
        fields["context"] = ContextId;
        Graph.AddObservation(kind, fields, ids);
    }
}
