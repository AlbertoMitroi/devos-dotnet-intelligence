using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotnetIntelligence.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// gRPC (4.3): .proto files with their role from the project's Protobuf items; service implementations (overrides
/// of a generated base bound by BindServiceMethod) as grpc.service; generated client calls as grpc.call. The fully
/// qualified service name is the generated __ServiceName constant. Client addresses are on the AddGrpcClient registration.
/// </summary>
public sealed partial class GrpcAdapter(AdapterContext context)
{
    public void VisitProtos(IReadOnlyList<WorkingCopyFile> repositoryFiles)
    {
        var projectFile = Path.Combine(context.Files.Root, context.Project.ProjectPath);
        var projectDirectory = Path.GetDirectoryName(context.Project.ProjectPath)!.Replace('\\', '/');
        XDocument document;
        try
        {
            document = XDocument.Load(projectFile, LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException)
        {
            return;
        }
        foreach (var item in document.Descendants().Where(e => e.Name.LocalName == "Protobuf"))
        {
            var include = item.Attribute("Include")?.Value;
            if (include is null)
            {
                continue;
            }
            var protoPath = Analysis.SolutionDiscovery.ResolveRelative(context.Project.ProjectPath, include);
            var role = item.Attribute("GrpcServices")?.Value ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "GrpcServices")?.Value ?? "Both";
            if (context.Files.BlobOf(protoPath) is not { } blob)
            {
                continue;
            }
            var text = File.ReadAllText(Path.Combine(context.Files.Root, protoPath));
            var package = Package().Match(text) is { Success: true } p ? p.Groups[1].Value : null;
            foreach (Match service in Service().Matches(text))
            {
                var fullName = package is null ? service.Groups[1].Value : package + "." + service.Groups[1].Value;
                var line = text[..service.Index].Count(c => c == '\n') + 1;
                var evidence = context.Graph.AddEvidence(
                    new Analysis.Graph.SourceLocation(protoPath, blob, new Analysis.Graph.SourceSpan(line, 1, line, service.Length + 1), "repository", null),
                    "config", context.ContextId, text.Split('\n')[line - 1]);
                var lineInfo = (System.Xml.IXmlLineInfo)item;
                var projectEvidence = context.Graph.AddEvidence(
                    new Analysis.Graph.SourceLocation(context.Project.ProjectPath, context.Files.BlobOf(context.Project.ProjectPath) ?? "unknown",
                        new Analysis.Graph.SourceSpan(lineInfo.LineNumber, lineInfo.LinePosition, lineInfo.LineNumber, lineInfo.LinePosition + 1), "repository", null),
                    "config", context.ContextId, null);
                context.Observe("grpc.proto", new JsonObject
                {
                    ["service"] = fullName,
                    ["proto"] = protoPath,
                    ["role"] = role.ToLowerInvariant(),
                    ["methods"] = new JsonArray(Rpc().Matches(service.Groups[2].Value).Select(m => (JsonNode)m.Groups[1].Value).ToArray()),
                }, evidence, projectEvidence);
            }
        }
    }

    public void VisitType(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.BaseType is not { } baseType
            || !baseType.GetAttributes().Any(a => AdapterContext.FullName(a.AttributeClass) == "Grpc.Core.BindServiceMethodAttribute")
            || ServiceName(baseType) is not { } serviceName)
        {
            return;
        }
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsOverride && SymbolEqualityComparer.Default.Equals(m.OverriddenMethod?.ContainingType, baseType)))
        {
            if (method.Locations.FirstOrDefault(l => l.IsInSource) is { } location)
            {
                context.Observe("grpc.service", new JsonObject
                {
                    ["service"] = serviceName,
                    ["method"] = method.Name,
                    ["implementation"] = context.IdOf(method),
                }, context.Evidence(location));
            }
        }
    }

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model)
    {
        var method = invocation.TargetMethod;
        if (method.ContainingType is not { } client || !AdapterContext.DerivesFrom(client.BaseType, "Grpc.Core.ClientBase") || ServiceName(client) is not { } serviceName)
        {
            return;
        }
        var rpc = method.Name.EndsWith("Async", StringComparison.Ordinal) && client.GetMembers(method.Name[..^5]).Any() ? method.Name[..^5] : method.Name;
        context.Observe("grpc.call", new JsonObject
        {
            ["service"] = serviceName,
            ["method"] = rpc,
            ["client"] = context.IdOf(client),
            ["caller"] = context.EnclosingMember(invocation.Syntax, model),
        }, context.Evidence(invocation.Syntax));
    }

    /// <summary>The generated "__ServiceName" field (static readonly, initialized with a literal) of the static class containing the generated base or client.</summary>
    private static string? ServiceName(INamedTypeSymbol generated)
    {
        var field = generated.ContainingType?.GetMembers("__ServiceName").OfType<IFieldSymbol>().FirstOrDefault();
        if (field is null)
        {
            return null;
        }
        if (field.HasConstantValue)
        {
            return field.ConstantValue as string;
        }
        return field.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>()
            .Select(v => v.Initializer?.Value).OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax>()
            .Select(l => l.Token.Value as string).FirstOrDefault(v => v is not null);
    }

    [GeneratedRegex(@"^\s*package\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex Package();

    [GeneratedRegex(@"\bservice\s+([A-Za-z0-9_]+)\s*\{((?:[^{}]|\{[^{}]*\})*)\}", RegexOptions.Singleline)]
    private static partial Regex Service();

    [GeneratedRegex(@"\brpc\s+([A-Za-z0-9_]+)\s*\(")]
    private static partial Regex Rpc();
}
