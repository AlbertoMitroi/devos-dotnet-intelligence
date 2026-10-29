using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;

namespace DotnetIntelligence.Analysis;

public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>States from the contract: failed, partial, unsupported, unknown. "Unknown is not empty."</summary>
public enum DiagnosticState { Failed, Partial, Unsupported, Unknown }

/// <param name="Scope">What the diagnostic is about: "repository", a repository-relative path, or a project path.</param>
public sealed record AnalysisDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Scope,
    DiagnosticState State,
    string Message)
{
    public JsonObject ToJson()
    {
        var fields = new JsonObject
        {
            ["code"] = Code,
            ["message"] = Message,
            ["scope"] = Scope,
            ["severity"] = Severity.ToString().ToLowerInvariant(),
            ["state"] = State.ToString().ToLowerInvariant(),
        };
        fields["id"] = Hashing.RecordId("diag", new JsonObject
        {
            ["code"] = Code,
            ["message"] = Message,
            ["scope"] = Scope,
        });
        return fields;
    }
}
