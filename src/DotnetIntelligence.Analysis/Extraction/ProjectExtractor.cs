using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Analysis.Extraction;

/// <summary>A call whose declared target may dispatch at run time (interface member or virtual member).</summary>
public sealed record DispatchSite(string From, string DeclaredTarget, string Context, string EvidenceId);

/// <summary>
/// Stage 7 (4.2.7): one pass per document with the semantic model: entities and every declaration (all parts of
/// partial types and members, generated documents with their generator), containment, references with usage kind,
/// calls to declared targets (constructors and extension methods resolved to their definition), and per type:
/// inheritance, interface implementations and overrides.
/// </summary>
public sealed class ProjectExtractor
{
    private const string Method = "semantic";

    private readonly LoadedProject _project;
    private readonly Compilation _compilation;
    private readonly string _context;
    private readonly string _projectId;
    private readonly GraphBuilder _graph;
    private readonly FileMap _files;
    private readonly SymbolIdentity _identity;
    private readonly ConcurrentBag<DispatchSite> _dispatchSites;

    public ProjectExtractor(LoadedProject project, string contextId, string projectId, GraphBuilder graph, FileMap files, SymbolIdentity identity, ConcurrentBag<DispatchSite> dispatchSites)
    {
        _project = project;
        _compilation = project.Compilation!;
        _context = contextId;
        _projectId = projectId;
        _graph = graph;
        _files = files;
        _identity = identity;
        _dispatchSites = dispatchSites;
    }

    public async Task ExtractAsync(CancellationToken cancellationToken)
    {
        foreach (var generated in await _project.Project!.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
        {
            var tree = await generated.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            if (tree is not null)
            {
                var match = _compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == tree.FilePath) ?? tree;
                _files.RegisterGenerated(match, GeneratorName(generated));
            }
        }

        await Parallel.ForEachAsync(_compilation.SyntaxTrees, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount },
            (tree, ct) =>
            {
                var model = _compilation.GetSemanticModel(tree);
                var root = tree.GetRoot(ct);
                foreach (var node in root.DescendantNodes())
                {
                    ct.ThrowIfCancellationRequested();
                    VisitDeclaration(node, model, ct);
                    VisitReference(node, model, ct);
                    VisitCall(node, model, ct);
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        foreach (var type in SourceTypes(_compilation.Assembly.GlobalNamespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractHierarchy(type);
        }
    }

    /// <summary>
    /// The generator type name: generated document paths end with "{generator assembly}/{generator type}/{hint name}",
    /// wherever the intermediate output directory is (obj/, artifacts/obj/, ...).
    /// </summary>
    private static string GeneratorName(SourceGeneratedDocument document)
    {
        var path = (document.FilePath ?? string.Empty).Replace('\\', '/');
        var hint = document.HintName.Replace('\\', '/');
        if (path.EndsWith("/" + hint, StringComparison.Ordinal))
        {
            var prefix = path[..^(hint.Length + 1)].Split('/');
            if (prefix.Length >= 2)
            {
                return prefix[^1];
            }
        }
        return "unknown-generator";
    }

    // ---- entities and declarations ----

    private string? EnsureEntity(ISymbol symbol)
    {
        var normalized = SymbolIdentity.Normalize(symbol);
        if (normalized is null)
        {
            return null;
        }
        if (normalized is INamespaceSymbol ns)
        {
            var project = _identity.ProjectOf(ns) ?? _project.ProjectPath;
            if (ns.IsGlobalNamespace)
            {
                return null;
            }
            var nsId = SymbolIdentity.NamespaceId(project, ns);
            _graph.AddEntity(nsId, () => new JsonObject
            {
                ["kind"] = "namespace",
                ["name"] = ns.Name,
                ["container"] = ns.ContainingNamespace is { IsGlobalNamespace: false } parent ? EnsureEntity(parent) : null,
                ["signature"] = ns.ToDisplayString(),
                ["accessibility"] = "public",
                ["modifiers"] = new JsonArray(),
                ["origin"] = "source",
                ["project"] = project,
            });
            return nsId;
        }
        var id = _identity.IdOf(normalized, _compilation);
        if (id is null || _graph.HasEntity(id))
        {
            return id;
        }
        var source = normalized.Locations.Any(l => l.IsInSource);
        string? container = normalized.ContainingType is { } containingType
            ? EnsureEntity(containingType)
            : source && normalized.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace ? EnsureEntity(containingNamespace) : null;
        _graph.AddEntity(id, () =>
        {
            var entity = new JsonObject
            {
                ["kind"] = SymbolIdentity.KindOf(normalized),
                ["name"] = normalized.MetadataName == ".ctor" || normalized.MetadataName == ".cctor" ? normalized.ContainingType?.Name ?? normalized.Name : normalized.Name,
                ["container"] = container,
                ["signature"] = normalized.ToDisplayString(SymbolIdentity.SignatureFormat),
                ["accessibility"] = SymbolIdentity.AccessibilityOf(normalized),
                ["modifiers"] = new JsonArray(SymbolIdentity.ModifiersOf(normalized).Select(m => (JsonNode)m).ToArray()),
                ["origin"] = OriginOf(normalized),
                ["documentationId"] = normalized.GetDocumentationCommentId(),
            };
            if (!source)
            {
                entity["assembly"] = normalized.ContainingAssembly?.Name;
                if (normalized.ContainingNamespace is { IsGlobalNamespace: false } metadataNamespace)
                {
                    entity["namespace"] = metadataNamespace.ToDisplayString();
                }
            }
            else
            {
                entity["project"] = _identity.ProjectOf(normalized);
            }
            return entity;
        });
        return id;
    }

    private string OriginOf(ISymbol symbol)
    {
        if (!symbol.Locations.Any(l => l.IsInSource))
        {
            return "metadata";
        }
        return symbol.Locations.Any(l => l.SourceTree is { } t && _files.IsCommitted(_files.Relative(Path.GetFullPath(t.FilePath)))) ? "source" : "generated";
    }

    private void VisitDeclaration(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        switch (node)
        {
            case BaseNamespaceDeclarationSyntax ns:
                Declare(model.GetDeclaredSymbol(ns, ct), ns.Name.GetLocation(), ns, kindContains: true);
                break;
            case BaseTypeDeclarationSyntax type:
                var typeSymbol = model.GetDeclaredSymbol(type, ct);
                Declare(typeSymbol, type.Identifier.GetLocation(), type, kindContains: true);
                if (typeSymbol is not null && type is TypeDeclarationSyntax { ParameterList: { } primary })
                {
                    var constructor = typeSymbol.InstanceConstructors.FirstOrDefault(c => c.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) == type));
                    Declare(constructor, primary.GetLocation(), primary, kindContains: true);
                    foreach (var parameter in primary.Parameters)
                    {
                        var property = typeSymbol.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>()
                            .FirstOrDefault(p => p.DeclaringSyntaxReferences.Any(r => r.GetSyntax(ct) == parameter));
                        Declare(property, parameter.Identifier.GetLocation(), parameter, kindContains: true);
                    }
                }
                break;
            case DelegateDeclarationSyntax d:
                Declare(model.GetDeclaredSymbol(d, ct), d.Identifier.GetLocation(), d, kindContains: true);
                break;
            case MethodDeclarationSyntax m:
                Declare(model.GetDeclaredSymbol(m, ct), m.Identifier.GetLocation(), m, kindContains: true);
                break;
            case ConstructorDeclarationSyntax c:
                Declare(model.GetDeclaredSymbol(c, ct), c.Identifier.GetLocation(), c, kindContains: true);
                break;
            case DestructorDeclarationSyntax d:
                Declare(model.GetDeclaredSymbol(d, ct), d.Identifier.GetLocation(), d, kindContains: true);
                break;
            case OperatorDeclarationSyntax o:
                Declare(model.GetDeclaredSymbol(o, ct), o.OperatorToken.GetLocation(), o, kindContains: true);
                break;
            case ConversionOperatorDeclarationSyntax c:
                Declare(model.GetDeclaredSymbol(c, ct), c.Type.GetLocation(), c, kindContains: true);
                break;
            case PropertyDeclarationSyntax p:
                Declare(model.GetDeclaredSymbol(p, ct), p.Identifier.GetLocation(), p, kindContains: true);
                break;
            case IndexerDeclarationSyntax i:
                Declare(model.GetDeclaredSymbol(i, ct), i.ThisKeyword.GetLocation(), i, kindContains: true);
                break;
            case EventDeclarationSyntax e:
                Declare(model.GetDeclaredSymbol(e, ct), e.Identifier.GetLocation(), e, kindContains: true);
                break;
            case EnumMemberDeclarationSyntax em:
                Declare(model.GetDeclaredSymbol(em, ct), em.Identifier.GetLocation(), em, kindContains: true);
                break;
            case VariableDeclaratorSyntax v when v.Parent?.Parent is BaseFieldDeclarationSyntax:
                Declare(model.GetDeclaredSymbol(v, ct), v.Identifier.GetLocation(), v.Parent.Parent, kindContains: true);
                break;
            case CompilationUnitSyntax unit when unit.Members.OfType<GlobalStatementSyntax>().FirstOrDefault() is { } first:
                // Top-level statements: the synthesized entry point and its Program type are declared by the statements.
                if (model.GetEnclosingSymbol(first.SpanStart, ct) is IMethodSymbol entryPoint)
                {
                    var last = unit.Members.OfType<GlobalStatementSyntax>().Last();
                    var extent = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(first.SpanStart, last.Span.End);
                    Declare(entryPoint.ContainingType, first.GetFirstToken().GetLocation(), unit, kindContains: false, Location.Create(unit.SyntaxTree, extent));
                    Declare(entryPoint, first.GetFirstToken().GetLocation(), unit, kindContains: true, Location.Create(unit.SyntaxTree, extent));
                }
                break;
        }
    }

    private void Declare(ISymbol? symbol, Location nameLocation, SyntaxNode extentNode, bool kindContains, Location? extentLocation = null)
    {
        if (symbol is null)
        {
            return;
        }
        var id = EnsureEntity(symbol);
        var location = _files.Locate(nameLocation);
        if (id is null || location is null)
        {
            return;
        }
        var extentSource = _files.Locate(extentLocation ?? extentNode.GetLocation());
        var extent = extentSource is not null && extentSource.Path == location.Path ? extentSource.Span : location.Span;
        _graph.AddDeclaration(id, _context, location, extent);
        var evidence = _graph.AddEvidence(location, Method, _context, _files.LineText(nameLocation, location));

        var normalized = SymbolIdentity.Normalize(symbol)!;
        if (normalized is INamedTypeSymbol && normalized.ContainingType is null)
        {
            _graph.AddRelation("declares", _projectId, id, _context, "exact", evidence);
        }
        if (kindContains)
        {
            var container = normalized.ContainingType is { } containingType ? EnsureEntity(containingType)
                : normalized.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace ? EnsureEntity(containingNamespace)
                : null;
            if (container is not null && container != id)
            {
                _graph.AddRelation("contains", container, id, _context, "exact", evidence);
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                foreach (var t in SourceTypes(child))
                {
                    yield return t;
                }
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (var t in WithNested(type))
                {
                    yield return t;
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        if (type.Locations.Any(l => l.IsInSource))
        {
            yield return type;
        }
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var t in WithNested(nested))
            {
                yield return t;
            }
        }
    }

    // ---- hierarchy ----

    private void ExtractHierarchy(INamedTypeSymbol type)
    {
        var typeId = EnsureEntity(type);
        if (typeId is null)
        {
            return;
        }
        var baseList = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<BaseTypeDeclarationSyntax>()
            .SelectMany(d => d.BaseList?.Types ?? default(SeparatedSyntaxList<BaseTypeSyntax>))
            .ToList();

        if (type.BaseType is { } baseType && baseType.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum or SpecialType.System_MulticastDelegate)
            && !(type.IsRecord && baseType.SpecialType == SpecialType.System_Object))
        {
            Relate("inherits", typeId, baseType, BaseListLocation(baseList, baseType) ?? type.Locations.FirstOrDefault(l => l.IsInSource));
        }
        foreach (var @interface in type.Interfaces)
        {
            Relate("implements", typeId, @interface, BaseListLocation(baseList, @interface) ?? type.Locations.FirstOrDefault(l => l.IsInSource));
        }

        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers().Where(m => m is IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }))
            {
                if (type.FindImplementationForInterfaceMember(member) is { } implementation
                    && SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type)
                    && EnsureEntity(implementation) is { } implementationId)
                {
                    Relate("implements", implementationId, member, implementation.Locations.FirstOrDefault(l => l.IsInSource));
                }
            }
        }

        foreach (var member in type.GetMembers())
        {
            ISymbol? overridden = member switch
            {
                IMethodSymbol m => m.OverriddenMethod,
                IPropertySymbol p => p.OverriddenProperty,
                IEventSymbol e => e.OverriddenEvent,
                _ => null,
            };
            if (overridden is not null && member.Locations.Any(l => l.IsInSource) && EnsureEntity(member) is { } memberId)
            {
                Relate("overrides", memberId, overridden, member.Locations.FirstOrDefault(l => l.IsInSource));
            }
        }
    }

    private Location? BaseListLocation(List<BaseTypeSyntax> baseList, INamedTypeSymbol target)
    {
        foreach (var item in baseList)
        {
            var model = _compilation.GetSemanticModel(item.SyntaxTree);
            if (model.GetTypeInfo(item.Type).Type is INamedTypeSymbol bound && SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, target.OriginalDefinition))
            {
                return item.Type.GetLocation();
            }
        }
        return null;
    }

    private void Relate(string kind, string from, ISymbol target, Location? location)
    {
        if (location is null || EnsureEntity(target) is not { } to || _files.Locate(location) is not { } source)
        {
            return;
        }
        var evidence = _graph.AddEvidence(source, Method, _context, _files.LineText(location, source));
        _graph.AddRelation(kind, from, to, _context, "exact", evidence);
    }

    // ---- references ----

    private void VisitReference(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        if (node is not SimpleNameSyntax name || name is IdentifierNameSyntax { IsVar: true } || IsInsideUsingDirective(name))
        {
            return;
        }
        var info = model.GetSymbolInfo(name, ct);
        var candidates = info.Symbol is { } single
            ? [(single, "exact")]
            : info.CandidateSymbols.Select(c => (c, info.CandidateReason is CandidateReason.Ambiguous or CandidateReason.OverloadResolutionFailure ? "ambiguous" : "candidate")).ToList();
        if (candidates.Count == 0)
        {
            return;
        }
        var from = EnclosingEntity(name, model, ct);
        if (from is null)
        {
            return;
        }
        Location? evidenceLocation = null;
        SourceLocation? source = null;
        foreach (var (candidate, resolution) in candidates)
        {
            var target = candidate is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor && IsAttributeName(name) ? ctor.ContainingType : candidate;
            var normalized = SymbolIdentity.Normalize(target);
            if (normalized is null or INamespaceSymbol || EnsureEntity(normalized) is not { } to || to == from)
            {
                continue;
            }
            evidenceLocation ??= name.GetLocation();
            source ??= _files.Locate(evidenceLocation);
            if (source is null)
            {
                return;
            }
            var evidence = _graph.AddEvidence(source, Method, _context, _files.LineText(evidenceLocation, source));
            _graph.AddRelation("references", from, to, _context, resolution, evidence, new JsonObject { ["usage"] = UsageOf(name, normalized, model, ct) });
        }
    }

    private static bool IsInsideUsingDirective(SyntaxNode node) => node.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null;

    private static bool IsAttributeName(SimpleNameSyntax name) =>
        name.Parent is AttributeSyntax || name.Parent is QualifiedNameSyntax { Parent: AttributeSyntax } qualified && qualified.Right == name;

    private static string UsageOf(SimpleNameSyntax name, ISymbol target, SemanticModel model, CancellationToken ct)
    {
        if (name.FirstAncestorOrSelf<TypeOfExpressionSyntax>() is not null)
        {
            return "typeof";
        }
        if (name.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } n
                && n != name && model.GetSymbolInfo(n, ct).Symbol is null && i.ArgumentList.Span.Contains(name.Span)))
        {
            return "nameof";
        }
        if (IsAttributeName(name))
        {
            return "attribute";
        }
        // The expression that stands for the name: "a.B" when the name is the right part of a member access.
        SyntaxNode expression = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access
            : name.Parent is MemberBindingExpressionSyntax binding ? binding
            : name;
        if (target is IMethodSymbol)
        {
            var parent = expression.Parent;
            if (parent is InvocationExpressionSyntax invocation && invocation.Expression == expression
                || parent is ConditionalAccessExpressionSyntax && expression is MemberBindingExpressionSyntax && expression.Parent?.Parent is InvocationExpressionSyntax)
            {
                return "invocation";
            }
            if (expression is MemberBindingExpressionSyntax && expression.Parent is InvocationExpressionSyntax)
            {
                return "invocation";
            }
            return "other";
        }
        if (target is IFieldSymbol or IPropertySymbol or IEventSymbol)
        {
            return expression.Parent switch
            {
                AssignmentExpressionSyntax assignment when assignment.Left == expression => "write",
                PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression } => "write",
                PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression } => "write",
                ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None) && !argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword) => "write",
                _ => "read",
            };
        }
        return "other";
    }

    /// <summary>The member or type a piece of code belongs to. Lambdas and local functions belong to their container (D6).</summary>
    private string? EnclosingEntity(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        foreach (var ancestor in node.Ancestors())
        {
            ISymbol? symbol = ancestor switch
            {
                AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax => null,
                VariableDeclaratorSyntax v when v.Parent?.Parent is BaseFieldDeclarationSyntax => model.GetDeclaredSymbol(v, ct),
                BaseFieldDeclarationSyntax => null,
                ParameterSyntax { Parent.Parent: TypeDeclarationSyntax } => null,
                BaseNamespaceDeclarationSyntax => null,
                MemberDeclarationSyntax member and not GlobalStatementSyntax => model.GetDeclaredSymbol(member, ct),
                GlobalStatementSyntax global => model.GetEnclosingSymbol(global.SpanStart, ct),
                _ => null,
            };
            if (symbol is not null)
            {
                return EnsureEntity(symbol);
            }
            if (ancestor is BaseNamespaceDeclarationSyntax)
            {
                return null;
            }
        }
        return null;
    }

    // ---- calls ----

    private void VisitCall(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        IMethodSymbol? target;
        Location location;
        bool viaBase = false;
        switch (node)
        {
            case InvocationExpressionSyntax invocation:
                if (model.GetOperation(invocation, ct) is not IInvocationOperation op)
                {
                    return;
                }
                target = op.TargetMethod;
                var callee = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax m => m.Name,
                    MemberBindingExpressionSyntax b => b.Name,
                    _ => invocation.Expression,
                };
                viaBase = invocation.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax };
                location = callee.GetLocation();
                break;
            case BaseObjectCreationExpressionSyntax creation:
                if (model.GetOperation(creation, ct) is not IObjectCreationOperation { Constructor: { } constructor })
                {
                    return;
                }
                target = constructor;
                location = creation is ObjectCreationExpressionSyntax explicitCreation ? explicitCreation.Type.GetLocation() : creation.NewKeyword.GetLocation();
                break;
            case ConstructorInitializerSyntax initializer:
                target = model.GetSymbolInfo(initializer, ct).Symbol as IMethodSymbol;
                location = initializer.ThisOrBaseKeyword.GetLocation();
                viaBase = true;
                break;
            case PrimaryConstructorBaseTypeSyntax primaryBase:
                target = model.GetSymbolInfo(primaryBase, ct).Symbol as IMethodSymbol;
                location = primaryBase.Type.GetLocation();
                viaBase = true;
                break;
            default:
                return;
        }
        if (target is null || EnclosingEntity(node, model, ct) is not { } from)
        {
            return;
        }
        var normalized = SymbolIdentity.Normalize(target);
        if (normalized is null || EnsureEntity(normalized) is not { } to || _files.Locate(location) is not { } source)
        {
            return;
        }
        var evidence = _graph.AddEvidence(source, Method, _context, _files.LineText(location, source));
        var fields = new JsonObject();
        if (target.IsExtensionMethod || target.ReducedFrom is not null)
        {
            fields["ext.dotnet.extensionMethod"] = true;
        }
        _graph.AddRelation("calls_declared_target", from, to, _context, "exact", evidence, fields.Count > 0 ? fields : null);
        if (!viaBase && !target.IsStatic && (target.IsVirtual || target.IsAbstract || target.IsOverride || target.ContainingType?.TypeKind == TypeKind.Interface))
        {
            _dispatchSites.Add(new DispatchSite(from, to, _context, evidence));
        }
    }
}
