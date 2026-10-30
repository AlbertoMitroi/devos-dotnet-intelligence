using DotnetIntelligence.Analysis;
using DotnetIntelligence.Analysis.Extraction;
using DotnetIntelligence.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <param name="Suggestions">Heuristic messaging candidates for suggested-conventions.yml.</param>
/// <param name="Diagnostics">Problems found while reading non-C# files (BPMN).</param>
/// <param name="RepositoryContext">The repository-scope analysis context (D22), present when files outside every project were read.</param>
public sealed record AdapterResult(SuggestionCollector Suggestions, IReadOnlyList<AnalysisDiagnostic> Diagnostics, System.Text.Json.Nodes.JsonObject? RepositoryContext);

/// <summary>Runs every adapter (4.3) over each semantically loaded project, after extraction.</summary>
public static class AdapterRunner
{
    public const string RepositoryScope = ".";

    public static AdapterResult Run(
        IReadOnlyList<(LoadedProject Project, string ContextId)> projects,
        IReadOnlyList<System.Text.Json.Nodes.JsonObject> contexts,
        GraphBuilder graph,
        FileMap files,
        SymbolIdentity identity,
        IReadOnlyList<WorkingCopyFile> repositoryFiles,
        Conventions conventions,
        CancellationToken cancellationToken,
        IReadOnlyList<DestinationTemplate>? inputTemplates = null)
    {
        var calls = new CallGraph(graph.Relations.Where(r => (string)r["kind"]! == "calls_declared_target")
            .Select(r => ((string)r["from"]!, (string)r["to"]!)));
        var suggestions = new SuggestionCollector();
        var repositoryProjects = repositoryFiles.Select(f => f.Path).Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        var projectGraph = new ProjectGraph(projects.Select(p => p.Project).ToList(), files);
        var configuration = new ConfigurationValues(files.Root, repositoryFiles, projectGraph);
        var adapterDiagnostics = new List<AnalysisDiagnostic>();
        AdapterContext Context(LoadedProject project, string contextId) => new(project, contextId, graph, files, identity)
        {
            Diagnostics = adapterDiagnostics,
            RepositoryProjects = repositoryProjects,
            RepositoryFiles = repositoryFiles,
            Configuration = configuration,
            Conventions = conventions,
        };

        // Pre-pass: options classes bound to configuration sections, in any project, before any adapter resolves a destination.
        foreach (var (project, contextId) in projects)
        {
            var context = Context(project, contextId);
            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                var model = context.Compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (BindingNames.Contains(Name(node)) && model.GetOperation(node, cancellationToken) is IInvocationOperation invocation)
                    {
                        configuration.VisitBinding(invocation, context);
                    }
                }
            }
        }

        var massTransit = new MassTransitAdapter();
        massTransit.UseRunners(projectGraph.Runners);
        var elasticsearch = new ElasticsearchAdapter();
        var abp = new AbpAdapter();
        abp.UseProjects(projectGraph);
        foreach (var (project, contextId) in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = Context(project, contextId);
            var mediatr = new MediatRAdapter(context);
            var marten = new MartenAdapter(context);
            var signalr = new SignalRAdapter(context);
            var workflow = new WorkflowHandlerAdapter(context);
            var di = new DependencyInjectionAdapter(context, calls);
            var web = new AspNetCoreAdapter(context);
            var http = new HttpClientAdapter(context);
            var grpc = new GrpcAdapter(context);
            var messaging = new MessagingAdapter(context, conventions, suggestions);
            var topology = new TopologyAdapter(context);
            var data = new DataAdapter(context);

            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                var model = context.Compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetOperation(node, cancellationToken) is not IInvocationOperation invocation)
                    {
                        continue;
                    }
                    di.VisitInvocation(invocation, model);
                    web.VisitInvocation(invocation);
                    http.VisitInvocation(invocation, model);
                    grpc.VisitInvocation(invocation, model);
                    messaging.VisitInvocation(invocation, model);
                    topology.VisitInvocation(invocation);
                    data.VisitInvocation(invocation, model);
                    massTransit.VisitInvocation(invocation, model, context);
                    mediatr.VisitInvocation(invocation, model);
                    marten.VisitInvocation(invocation, model);
                    signalr.VisitInvocation(invocation, model);
                    elasticsearch.VisitInvocation(invocation, context);
                    abp.VisitInvocation(invocation, model, context);
                }
            }
            foreach (var type in SourceTypes(context.Compilation.Assembly.GlobalNamespace))
            {
                di.VisitType(type);
                web.VisitType(type);
                grpc.VisitType(type);
                messaging.VisitType(type);
                data.VisitType(type);
                massTransit.VisitType(type, context);
                mediatr.VisitType(type);
                workflow.VisitType(type);
                abp.VisitType(type, context);
            }
            data.Complete(tree => context.Compilation.GetSemanticModel(tree));
            web.Complete();
            grpc.VisitProtos(repositoryFiles);
            new ConfigurationAdapter(context).Visit(repositoryFiles);
        }
        massTransit.Emit();
        elasticsearch.Emit();
        abp.Emit();

        // C7 call sites, after every template of this run exists.
        var templates = CallArgumentAdapter.SourceTemplates(graph).Concat(inputTemplates ?? []).ToList();
        foreach (var (project, contextId) in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallArgumentAdapter.Visit(Context(project, contextId), templates, cancellationToken);
        }

        var (diagnostics, repositoryContext) = ReadFiles(contexts, graph, files, repositoryFiles, conventions);
        diagnostics.AddRange(adapterDiagnostics);
        AddMissingMetadataEntities(projects, graph, identity);
        return new AdapterResult(suggestions, diagnostics, repositoryContext);
    }

    /// <summary>
    /// Every entity id an observation names has an entity record (3.6). Source symbols are declared by extraction; a
    /// metadata symbol that only an adapter names (for example a constructed ABP event type) gets its record here, found
    /// by documentation comment id in the compilation of the observation's context.
    /// </summary>
    private static void AddMissingMetadataEntities(IReadOnlyList<(LoadedProject Project, string ContextId)> projects, GraphBuilder graph, SymbolIdentity identity)
    {
        var compilations = projects.Where(p => p.Project.Compilation is not null).ToDictionary(p => p.ContextId, p => p.Project.Compilation!, StringComparer.Ordinal);
        foreach (var observation in graph.Observations.OrderBy(o => (string)o["id"]!, StringComparer.Ordinal).ToList())
        {
            if ((string?)observation["context"] is not { } context || !compilations.TryGetValue(context, out var compilation))
            {
                continue;
            }
            foreach (var id in EntityIds(observation).Where(id => id.StartsWith("dotnet:pkg:", StringComparison.Ordinal) && !graph.HasEntity(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var documentationId = id[(id.IndexOf(':', id.IndexOf(':', "dotnet:pkg:".Length) + 1) + 1)..];
                if (DocumentationCommentId.GetFirstSymbolForDeclarationId(documentationId, compilation) is { } symbol && identity.IdOf(symbol, compilation) == id)
                {
                    AddMetadataEntity(graph, identity, compilation, symbol);
                }
            }
        }
    }

    private static string? AddMetadataEntity(GraphBuilder graph, SymbolIdentity identity, Compilation compilation, ISymbol symbol)
    {
        if (identity.IdOf(symbol, compilation) is not { } id || graph.HasEntity(id) || symbol.Locations.Any(l => l.IsInSource))
        {
            return identity.IdOf(symbol, compilation);
        }
        var container = symbol.ContainingType is { } containingType ? AddMetadataEntity(graph, identity, compilation, containingType) : null;
        graph.AddEntity(id, () =>
        {
            var entity = new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = SymbolIdentity.KindOf(symbol),
                ["name"] = symbol.Name,
                ["container"] = container,
                ["signature"] = symbol.ToDisplayString(SymbolIdentity.SignatureFormat),
                ["accessibility"] = SymbolIdentity.AccessibilityOf(symbol),
                ["modifiers"] = new System.Text.Json.Nodes.JsonArray(SymbolIdentity.ModifiersOf(symbol).Select(m => (System.Text.Json.Nodes.JsonNode)m).ToArray()),
                ["origin"] = "metadata",
                ["documentationId"] = symbol.GetDocumentationCommentId(),
                ["assembly"] = symbol.ContainingAssembly?.Name,
            };
            if (symbol.ContainingNamespace is { IsGlobalNamespace: false } metadataNamespace)
            {
                entity["namespace"] = metadataNamespace.ToDisplayString();
            }
            return entity;
        });
        return id;
    }

    private static IEnumerable<string> EntityIds(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        System.Text.Json.Nodes.JsonObject obj => obj.SelectMany(p => EntityIds(p.Value)),
        System.Text.Json.Nodes.JsonArray array => array.SelectMany(EntityIds),
        System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var text) && text.StartsWith("dotnet:", StringComparison.Ordinal) => [text],
        _ => [],
    };

    /// <summary>
    /// BPMN and Terraform files: owned by the deepest project whose directory contains them, else by the repository
    /// scope (project ".", its own analysis context, D22).
    /// </summary>
    private static (List<AnalysisDiagnostic> Diagnostics, System.Text.Json.Nodes.JsonObject? RepositoryContext) ReadFiles(
        IReadOnlyList<System.Text.Json.Nodes.JsonObject> contexts, GraphBuilder graph, FileMap files, IReadOnlyList<WorkingCopyFile> repositoryFiles, Conventions conventions)
    {
        var diagnostics = new List<AnalysisDiagnostic>();
        var owners = contexts.Select(c => (Directory: Path.GetDirectoryName((string)c["project"]!)!.Replace('\\', '/'), Project: (string)c["project"]!, Context: (string)c["id"]!))
            .OrderByDescending(o => o.Directory.Length).ThenBy(o => o.Project, StringComparer.Ordinal).ToList();
        var repositoryContextId = Contracts.Hashing.RecordId("ctx", new System.Text.Json.Nodes.JsonObject { ["configuration"] = "repository", ["project"] = RepositoryScope });
        var usedRepositoryScope = false;
        FileScope ScopeOf(string path)
        {
            foreach (var owner in owners)
            {
                if (owner.Directory.Length == 0 || path.StartsWith(owner.Directory + "/", StringComparison.Ordinal))
                {
                    return new FileScope(graph, owner.Project, owner.Context);
                }
            }
            usedRepositoryScope = true;
            return new FileScope(graph, RepositoryScope, repositoryContextId);
        }
        var byPath = repositoryFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var file in repositoryFiles.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (file.Path.EndsWith(".bpmn", StringComparison.OrdinalIgnoreCase))
            {
                BpmnReader.Read(file, files.Root, byPath, ScopeOf(file.Path), conventions.Workflow, diagnostics);
            }
            else if (file.Path.EndsWith(".tf", StringComparison.OrdinalIgnoreCase))
            {
                TerraformReader.Read(file, files.Root, ScopeOf(file.Path));
            }
        }
        var repositoryContext = !usedRepositoryScope ? null : new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = repositoryContextId,
            ["recordType"] = "analysisContext",
            ["project"] = RepositoryScope,
            ["projectId"] = Contracts.Hashing.RecordId("prj", new System.Text.Json.Nodes.JsonObject { ["path"] = RepositoryScope }),
            ["configuration"] = "repository",
            ["loadMode"] = "syntax",
            ["reason"] = "Files outside every project (BPMN, Terraform), read without a compilation.",
            ["scope"] = "repository",
        };
        return (diagnostics, repositoryContext);
    }

    private static readonly HashSet<string> BindingNames = new(StringComparer.Ordinal) { "Configure", "Bind", "BindConfiguration", "Get" };

    private static string Name(InvocationExpressionSyntax node) => node.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => string.Empty,
    };

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                foreach (var t in SourceTypes(child)) yield return t;
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (var t in WithNested(type)) yield return t;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
    {
        if (type.Locations.Any(l => l.IsInSource)) yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var t in WithNested(nested)) yield return t;
        }
    }
}
