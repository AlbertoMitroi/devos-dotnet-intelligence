using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis;
using DotnetIntelligence.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// One destination parameter of a helper (contract v1.2, C7): calls of any of <see cref="Callees"/> (source entity ids,
/// basis source) or of the documentation ids <see cref="DocumentationIds"/> of package <see cref="PackageId"/> (basis
/// generation) pass the destination as argument <see cref="Ordinal"/>.
/// </summary>
public sealed record DestinationTemplate(
    string Basis,
    IReadOnlySet<string> Callees,
    string? PackageId,
    IReadOnlySet<string> DocumentationIds,
    string Parameter,
    int Ordinal,
    string RoleKind,
    string Field)
{
    public IEnumerable<string> Names =>
        Callees.Concat(DocumentationIds).Select(id => id.Split('(', 2)[0]).Select(id => id[(id.LastIndexOf('.') + 1)..]).Distinct(StringComparer.Ordinal);
}

/// <summary>
/// C7 call sites: one call.argument observation per call of a member whose parameter is a known destination parameter,
/// with the argument's constant value (literal, constant or nameof) or the reason it is unknown. Templates come from
/// this run's observations (basis source) and from input generations of the helpers (basis generation). Only those
/// parameters are read; no other argument value is stored, and a value the secret check flags is not stored (D20).
/// </summary>
public static class CallArgumentAdapter
{
    /// <summary>Observation fields that name the member containing a template's call.</summary>
    private static readonly string[] MemberFields = ["sender", "publisher", "caller"];

    /// <summary>Templates of this run: the member that contains the destination, plus the interface members it implements.</summary>
    public static List<DestinationTemplate> SourceTemplates(GraphBuilder graph)
    {
        var implemented = graph.Relations.Where(r => (string?)r["kind"] == "implements")
            .GroupBy(r => (string)r["from"]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => (string)r["to"]!).ToList(), StringComparer.Ordinal);
        return Templates(graph.Observations, implemented, "source", _ => null);
    }

    /// <summary>
    /// Templates from the generation of the repository that produces the helper (basis generation): the member and the
    /// interface members it implements, as documentation ids of the producing project's package.
    /// </summary>
    public static List<DestinationTemplate> GenerationTemplates(string generation)
    {
        static IEnumerable<JsonObject> Read(string path) =>
            File.Exists(path) ? File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()) : [];
        var packages = Read(Path.Combine(generation, "graph", "projects.ndjson"))
            .Where(p => (string?)p["recordType"] == "project" && (string?)p["packageId"] is not null)
            .ToDictionary(p => (string)p["path"]!, p => (string)p["packageId"]!, StringComparer.Ordinal);
        var implemented = Read(Path.Combine(generation, "graph", "relations.ndjson")).Where(r => (string?)r["kind"] == "implements")
            .GroupBy(r => (string)r["from"]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => (string)r["to"]!).ToList(), StringComparer.Ordinal);
        return Templates(Read(Path.Combine(generation, "graph", "observations.ndjson")), implemented, "generation", id =>
        {
            var csproj = id.IndexOf(".csproj:", StringComparison.Ordinal);
            return csproj > 0 && packages.TryGetValue(id["dotnet:".Length..(csproj + ".csproj".Length)], out var package) ? package : null;
        });
    }

    private static List<DestinationTemplate> Templates(IEnumerable<JsonObject> observations, Dictionary<string, List<string>> implemented, string basis, Func<string, string?> packageOf)
    {
        var templates = new List<DestinationTemplate>();
        foreach (var observation in observations.Where(o => o["destinationParameter"] is JsonObject).OrderBy(o => (string)o["id"]!, StringComparer.Ordinal))
        {
            var member = MemberFields.Select(f => (string?)(observation[f] as JsonValue)).FirstOrDefault(m => m is not null);
            if (member is null)
            {
                continue;
            }
            var destination = observation["destinationParameter"]!;
            var callees = new SortedSet<string>(StringComparer.Ordinal) { member };
            callees.UnionWith(implemented.GetValueOrDefault(member) ?? []);
            var package = basis == "generation" ? packageOf(member) : null;
            if (basis == "generation" && package is null)
            {
                continue;
            }
            templates.Add(new DestinationTemplate(basis,
                basis == "source" ? callees : new SortedSet<string>(StringComparer.Ordinal),
                package,
                basis == "generation" ? new SortedSet<string>(callees.Select(DocumentationId), StringComparer.Ordinal) : new SortedSet<string>(StringComparer.Ordinal),
                (string)destination["parameter"]!, (int)destination["ordinal"]!, (string)observation["kind"]!, (string)destination["field"]!));
        }
        return templates;
    }

    /// <summary>"dotnet:{project}:{documentation id}" or "dotnet:pkg:{package}@{version}:{assembly}:{documentation id}" gives the documentation id.</summary>
    internal static string DocumentationId(string id)
    {
        var csproj = id.IndexOf(".csproj:", StringComparison.Ordinal);
        if (csproj > 0)
        {
            return id[(csproj + ".csproj:".Length)..];
        }
        if (id.StartsWith("dotnet:pkg:", StringComparison.Ordinal))
        {
            var afterPackage = id.IndexOf(':', "dotnet:pkg:".Length);
            var afterAssembly = afterPackage < 0 ? -1 : id.IndexOf(':', afterPackage + 1);
            return afterAssembly < 0 ? id : id[(afterAssembly + 1)..];
        }
        return id;
    }

    /// <summary>The package id of "dotnet:pkg:{package}@{version}:...", else null.</summary>
    internal static string? PackageOf(string id)
    {
        if (!id.StartsWith("dotnet:pkg:", StringComparison.Ordinal))
        {
            return null;
        }
        var end = id.IndexOf(':', "dotnet:pkg:".Length);
        var package = end < 0 ? null : id["dotnet:pkg:".Length..end];
        return package?[..package.LastIndexOf('@')];
    }

    public static void Visit(AdapterContext context, IReadOnlyList<DestinationTemplate> templates, CancellationToken cancellationToken)
    {
        if (templates.Count == 0)
        {
            return;
        }
        var names = templates.SelectMany(t => t.Names).ToHashSet(StringComparer.Ordinal);
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            var model = context.Compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = node.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    SimpleNameSyntax simple => simple.Identifier.ValueText,
                    _ => null,
                };
                if (name is null || !names.Contains(name) || model.GetOperation(node, cancellationToken) is not IInvocationOperation invocation
                    || context.IdOf(invocation.TargetMethod) is not { } callee)
                {
                    continue;
                }
                foreach (var template in templates.Where(t => t.Callees.Contains(callee)
                             || t.PackageId is not null && PackageOf(callee) == t.PackageId && t.DocumentationIds.Contains(DocumentationId(callee))))
                {
                    Observe(context, model, invocation, callee, template);
                }
            }
        }
    }

    private static void Observe(AdapterContext context, SemanticModel model, IInvocationOperation invocation, string callee, DestinationTemplate template)
    {
        if (context.EnclosingMember(invocation.Syntax, model) is not { } caller)
        {
            return;
        }
        var argument = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == template.Ordinal && a.ArgumentKind != ArgumentKind.DefaultValue);
        var value = argument is null ? null : AdapterContext.Unwrap(argument.Value);
        var (text, source) = value switch
        {
            INameOfOperation nameOf when nameOf.ConstantValue is { HasValue: true, Value: string s } => (s, "nameof"),
            ILiteralOperation literal when literal.ConstantValue is { HasValue: true, Value: string s } => (s, "literal"),
            { ConstantValue: { HasValue: true, Value: string s } } => (s, "constant"),
            _ => ((string?)null, "unknown"),
        };
        string? reason = null;
        if (text is null)
        {
            reason = argument is null ? $"The call does not pass parameter '{template.Parameter}'." : "The argument is not a compile-time constant string.";
        }
        else if (Analysis.SecretScrubber.LooksSecret(template.Parameter, text))
        {
            (text, reason) = (null, "secret");
        }
        var fields = new JsonObject
        {
            ["caller"] = caller,
            ["callee"] = callee,
            ["parameter"] = template.Parameter,
            ["ordinal"] = template.Ordinal,
            ["value"] = text,
            ["valueSource"] = text is null && reason != "secret" ? "unknown" : source,
            ["role"] = new JsonObject { ["kind"] = template.RoleKind, ["field"] = template.Field },
            ["basis"] = template.Basis,
            ["resolution"] = text is null ? "unresolved" : "exact",
        };
        if (reason is not null)
        {
            fields["reason"] = reason;
        }
        context.Observe("call.argument", fields, context.Evidence(invocation.Syntax));
    }
}
