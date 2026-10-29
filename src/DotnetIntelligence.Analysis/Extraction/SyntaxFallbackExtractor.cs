using System.Text;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetIntelligence.Analysis.Extraction;

/// <summary>
/// Stage 8 (4.2.8): for projects that were not loaded semantically, Roslyn's parser only: declarations and
/// containment, method "syntax". Type, field, property and event ids are documentation comment ids built from the
/// syntax (exact for the names as written). Method ids cannot be exact without binding parameter types; they carry
/// the parameter types as written after "#syntax" and are marked with ext.dotnet.syntaxSignature.
/// </summary>
public sealed class SyntaxFallbackExtractor(string projectPath, string contextId, string projectId, GraphBuilder graph, FileMap files)
{
    private const string Method = "syntax";

    public int Extract(IEnumerable<WorkingCopyFile> projectFiles, IReadOnlyList<string> preprocessorSymbols)
    {
        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithPreprocessorSymbols(preprocessorSymbols);
        var count = 0;
        foreach (var file in projectFiles)
        {
            var absolute = Path.Combine(files.Root, file.Path);
            var text = File.ReadAllText(absolute);
            var tree = CSharpSyntaxTree.ParseText(text, options, absolute);
            Visit(tree.GetCompilationUnitRoot(), string.Empty, null, file);
            count++;
        }
        return count;
    }

    private void Visit(SyntaxNode node, string namespaceName, (string Id, string DocName)? containingType, WorkingCopyFile file)
    {
        foreach (var child in node.ChildNodes())
        {
            switch (child)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    var name = namespaceName.Length == 0 ? ns.Name.ToString() : namespaceName + "." + ns.Name;
                    var nsId = $"dotnet:{projectPath}:N:{name}";
                    Add(nsId, "namespace", ns.Name.ToString().Split('.').Last(), namespaceName.Length == 0 ? null : $"dotnet:{projectPath}:N:{namespaceName}", name, "public", default, ns.Name.GetLocation(), ns, file);
                    Visit(ns, name, null, file);
                    break;
                case BaseTypeDeclarationSyntax type:
                    var arity = type is TypeDeclarationSyntax { TypeParameterList: { } tp } ? "`" + tp.Parameters.Count : string.Empty;
                    var docName = containingType is { } outer
                        ? outer.DocName + "." + type.Identifier.ValueText + arity
                        : (namespaceName.Length == 0 ? string.Empty : namespaceName + ".") + type.Identifier.ValueText + arity;
                    var typeId = $"dotnet:{projectPath}:T:{docName}";
                    Add(typeId, KindOf(type), type.Identifier.ValueText, containingType?.Id ?? ParentNamespaceId(namespaceName + ".x"),
                        docName, Accessibility(type.Modifiers, containingType is null), type.Modifiers, type.Identifier.GetLocation(), type, file);
                    if (containingType is null)
                    {
                        graph.AddRelation("declares", projectId, typeId, contextId, "exact", Evidence(type.Identifier.GetLocation(), file));
                    }
                    if (type is EnumDeclarationSyntax e)
                    {
                        foreach (var member in e.Members)
                        {
                            Add($"dotnet:{projectPath}:F:{docName}.{member.Identifier.ValueText}", "enum_member", member.Identifier.ValueText, typeId,
                                docName + "." + member.Identifier.ValueText, "public", default, member.Identifier.GetLocation(), member, file);
                        }
                    }
                    Visit(type, namespaceName, (typeId, docName), file);
                    break;
                case MemberDeclarationSyntax member when containingType is { } owner:
                    VisitMember(member, owner, file);
                    break;
            }
        }
    }

    private void VisitMember(MemberDeclarationSyntax member, (string Id, string DocName) owner, WorkingCopyFile file)
    {
        switch (member)
        {
            case MethodDeclarationSyntax m:
                var generic = m.TypeParameterList is { } tp ? "``" + tp.Parameters.Count : string.Empty;
                AddMethod(owner, m.Identifier.ValueText + generic, m.Identifier.ValueText, m.ParameterList, m.Modifiers, m.Identifier.GetLocation(), m, file, "method");
                break;
            case ConstructorDeclarationSyntax c:
                AddMethod(owner, c.Modifiers.Any(SyntaxKind.StaticKeyword) ? "#cctor" : "#ctor", c.Identifier.ValueText, c.ParameterList, c.Modifiers, c.Identifier.GetLocation(), c, file, "constructor");
                break;
            case PropertyDeclarationSyntax p:
                Add($"dotnet:{projectPath}:P:{owner.DocName}.{p.Identifier.ValueText}", "property", p.Identifier.ValueText, owner.Id,
                    owner.DocName + "." + p.Identifier.ValueText, Accessibility(p.Modifiers, false), p.Modifiers, p.Identifier.GetLocation(), p, file);
                break;
            case EventDeclarationSyntax ev:
                Add($"dotnet:{projectPath}:E:{owner.DocName}.{ev.Identifier.ValueText}", "event", ev.Identifier.ValueText, owner.Id,
                    owner.DocName + "." + ev.Identifier.ValueText, Accessibility(ev.Modifiers, false), ev.Modifiers, ev.Identifier.GetLocation(), ev, file);
                break;
            case BaseFieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    var prefix = field is EventFieldDeclarationSyntax ? "E" : "F";
                    var kind = field is EventFieldDeclarationSyntax ? "event" : field.Modifiers.Any(SyntaxKind.ConstKeyword) ? "constant" : "field";
                    Add($"dotnet:{projectPath}:{prefix}:{owner.DocName}.{variable.Identifier.ValueText}", kind, variable.Identifier.ValueText, owner.Id,
                        owner.DocName + "." + variable.Identifier.ValueText, Accessibility(field.Modifiers, false), field.Modifiers, variable.Identifier.GetLocation(), field, file);
                }
                break;
        }
    }

    private void AddMethod((string Id, string DocName) owner, string docName, string name, ParameterListSyntax parameters, SyntaxTokenList modifiers,
        Location location, SyntaxNode node, WorkingCopyFile file, string kind)
    {
        var written = string.Join(",", parameters.Parameters.Select(p => p.Type?.ToString().Replace(" ", string.Empty) ?? "?"));
        var id = $"dotnet:{projectPath}:M:{owner.DocName}.{docName}#syntax({written})";
        Add(id, kind, name, owner.Id, $"{owner.DocName}.{name}({written})", Accessibility(modifiers, false), modifiers, location, node, file,
            new JsonObject { ["ext.dotnet.syntaxSignature"] = true });
    }

    private string? ParentNamespaceId(string qualifiedChild)
    {
        var dot = qualifiedChild.LastIndexOf('.');
        return dot <= 0 ? null : $"dotnet:{projectPath}:N:{qualifiedChild[..dot]}";
    }

    private void Add(string id, string kind, string name, string? container, string signature, string accessibility, SyntaxTokenList modifiers,
        Location location, SyntaxNode node, WorkingCopyFile file, JsonObject? extra = null)
    {
        graph.AddEntity(id, () =>
        {
            var entity = new JsonObject
            {
                ["kind"] = kind,
                ["name"] = name,
                ["container"] = container,
                ["signature"] = signature,
                ["accessibility"] = accessibility,
                ["modifiers"] = new JsonArray(modifiers.Select(m => m.ValueText)
                    .Where(m => m is not ("public" or "private" or "protected" or "internal" or "file")).Order(StringComparer.Ordinal).Select(m => (JsonNode)m).ToArray()),
                ["origin"] = "source",
                ["project"] = projectPath,
                ["documentationId"] = null,
            };
            if (extra is not null)
            {
                foreach (var (key, value) in extra)
                {
                    entity[key] = value?.DeepClone();
                }
            }
            return entity;
        });
        var source = Locate(location, file);
        var extent = Locate(node.GetLocation(), file);
        graph.AddDeclaration(id, contextId, source, extent.Span);
        var evidence = graph.AddEvidence(source, Method, contextId, LineOf(location));
        if (container is not null)
        {
            graph.AddRelation("contains", container, id, contextId, "exact", evidence);
        }
    }

    private string Evidence(Location location, WorkingCopyFile file) =>
        graph.AddEvidence(Locate(location, file), Method, contextId, LineOf(location));

    private static SourceLocation Locate(Location location, WorkingCopyFile file) =>
        new(file.Path, file.BlobId, FileMap.ToSpan(location.GetLineSpan().Span), "repository", null);

    private static string LineOf(Location location)
    {
        var text = location.SourceTree!.GetText();
        return text.ToString(text.Lines.GetLineFromPosition(location.SourceSpan.Start).Span);
    }

    private static string KindOf(BaseTypeDeclarationSyntax type) => type switch
    {
        RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record_struct" : "record",
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        _ => "type",
    };

    private static string Accessibility(SyntaxTokenList modifiers, bool topLevel)
    {
        var has = (SyntaxKind k) => modifiers.Any(k);
        if (has(SyntaxKind.PublicKeyword)) return "public";
        if (has(SyntaxKind.ProtectedKeyword) && has(SyntaxKind.InternalKeyword)) return "protected_internal";
        if (has(SyntaxKind.PrivateKeyword) && has(SyntaxKind.ProtectedKeyword)) return "private_protected";
        if (has(SyntaxKind.ProtectedKeyword)) return "protected";
        if (has(SyntaxKind.InternalKeyword)) return "internal";
        if (has(SyntaxKind.PrivateKeyword)) return "private";
        return topLevel ? "internal" : "private";
    }
}
