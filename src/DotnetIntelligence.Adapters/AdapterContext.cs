using System.Text;
using System.Text.Json.Nodes;
using DotnetIntelligence.Analysis;
using DotnetIntelligence.Analysis.Extraction;
using DotnetIntelligence.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>Shared state and helpers for the framework adapters of one analysis context (4.3).</summary>
public sealed class AdapterContext
{
    public AdapterContext(LoadedProject project, string contextId, GraphBuilder graph, FileMap files, SymbolIdentity identity, string method = "semantic")
    {
        Project = project;
        Compilation = project.Compilation!;
        ContextId = contextId;
        Graph = graph;
        Files = files;
        Identity = identity;
        Method = method;
    }

    public LoadedProject Project { get; }
    public Compilation Compilation { get; }
    public string ContextId { get; }
    public GraphBuilder Graph { get; }
    public FileMap Files { get; }
    public SymbolIdentity Identity { get; }
    public string Method { get; }

    /// <summary>Repository-relative paths of every C# project file in the commit.</summary>
    public IReadOnlySet<string> RepositoryProjects { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Every file of the analysed commit.</summary>
    public IReadOnlyList<WorkingCopyFile> RepositoryFiles { get; init; } = [];

    /// <summary>Configuration values of the repository, used only for destinations (D21).</summary>
    public ConfigurationValues? Configuration { get; init; }

    /// <summary>Problems adapters report (for example an open generic handler without a constraint); shared by every context of a run.</summary>
    public List<AnalysisDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Caller conventions (configuration file).</summary>
    public Conventions Conventions { get; init; } = new();

    public string? IdOf(ISymbol? symbol) =>
        SymbolIdentity.Normalize(symbol) is { } normalized && normalized is not INamespaceSymbol ? Identity.IdOf(normalized, Compilation) : null;

    public string? Evidence(SyntaxNode node, string? method = null) => Evidence(node.GetLocation(), method);

    public string? Evidence(Location location, string? method = null)
    {
        if (Files.Locate(location) is not { } source)
        {
            return null;
        }
        return Graph.AddEvidence(source, method ?? Method, ContextId, Files.LineText(location, source));
    }

    /// <summary>Adds an observation with the common fields (project, context) and its evidence; nothing without evidence (rule 1.4.4).</summary>
    public void Observe(string kind, JsonObject fields, params string?[] evidence)
    {
        var ids = evidence.Where(e => e is not null).Select(e => e!).ToList();
        if (ids.Count == 0)
        {
            return;
        }
        if (fields.ContainsKey("project") || fields.ContainsKey("context"))
        {
            throw new InvalidOperationException($"Observation fields 'project' and 'context' are reserved ({kind}).");
        }
        fields["project"] = Project.ProjectPath;
        fields["context"] = ContextId;
        Graph.AddObservation(kind, fields, ids);
    }

    // ---- symbol matching ----

    public static string FullName(ISymbol? symbol) =>
        symbol?.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
            .WithGenericsOptions(SymbolDisplayGenericsOptions.None)) ?? string.Empty;

    public static bool IsMethodOf(IMethodSymbol method, string containingType, params string[] names) =>
        FullName(method.ContainingType) == containingType && (names.Length == 0 || names.Contains(method.Name));

    public static bool DerivesFrom(ITypeSymbol? type, string baseFullName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (FullName(current) == baseFullName)
            {
                return true;
            }
        }
        return false;
    }

    public static bool Implements(ITypeSymbol? type, string interfaceFullName) =>
        type is not null && (FullName(type) == interfaceFullName || type.AllInterfaces.Any(i => FullName(i) == interfaceFullName));

    // ---- operations ----

    public static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                case IArgumentOperation argument:
                    operation = argument.Value;
                    continue;
                default:
                    return operation;
            }
        }
    }

    /// <summary>The receiver of an invocation, including the "this" argument of an extension method.</summary>
    public static IOperation? Receiver(IInvocationOperation invocation) =>
        invocation.Instance is { } instance ? Unwrap(instance)
        : invocation.TargetMethod.IsExtensionMethod && invocation.Arguments.Length > 0 ? Unwrap(invocation.Arguments[0].Value)
        : null;

    /// <summary>Argument by parameter name (named or positional), unwrapped.</summary>
    public static IOperation? Argument(IInvocationOperation invocation, string parameterName) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameterName && a.ArgumentKind != ArgumentKind.DefaultValue) is { } a ? Unwrap(a.Value) : null;

    /// <summary>The single initializer of a local, when the local is never reassigned.</summary>
    public IOperation? LocalValue(ILocalSymbol local)
    {
        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } value } declarator)
            {
                var body = declarator.FirstAncestorOrSelf<MemberDeclarationSyntax>() as SyntaxNode ?? declarator.SyntaxTree.GetRoot();
                var reassigned = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Any(a => a.Left is IdentifierNameSyntax id && id.Identifier.ValueText == local.Name);
                if (reassigned)
                {
                    return null;
                }
                var model = Compilation.GetSemanticModel(value.SyntaxTree);
                return model.GetOperation(value) is { } op ? Unwrap(op) : null;
            }
        }
        return null;
    }

    /// <summary>
    /// A string value as a template: constants folded by the semantic model, interpolation holes and unknown parts
    /// become "{}" (resolved = false when nothing constant was found).
    /// </summary>
    public (string? Template, bool Constant) StringTemplate(IOperation? operation)
    {
        if (operation is null)
        {
            return (null, false);
        }
        operation = Unwrap(operation);
        if (operation.ConstantValue is { HasValue: true, Value: string constant })
        {
            return (constant, true);
        }
        switch (operation)
        {
            case IInterpolatedStringOperation interpolated:
                var builder = new StringBuilder();
                var allConstant = true;
                foreach (var part in interpolated.Parts)
                {
                    if (part is IInterpolatedStringTextOperation text && text.Text.ConstantValue.Value is string literal)
                    {
                        builder.Append(literal);
                    }
                    else if (part is IInterpolationOperation hole && hole.FormatString is null && hole.Alignment is null
                        && StringTemplate(hole.Expression) is { Template: { } holeTemplate, Constant: true })
                    {
                        builder.Append(holeTemplate);
                    }
                    else
                    {
                        builder.Append("{}");
                        allConstant = false;
                    }
                }
                return (builder.ToString(), allConstant);
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } binary when binary.Type?.SpecialType == SpecialType.System_String:
                var (left, leftConstant) = StringTemplate(binary.LeftOperand);
                var (right, rightConstant) = StringTemplate(binary.RightOperand);
                return ((left ?? "{}") + (right ?? "{}"), leftConstant && rightConstant);
            case ILocalReferenceOperation local when LocalValue(local.Local) is { } value:
                return StringTemplate(value);
            case IFieldReferenceOperation field when FieldInitializer(field.Field) is { } initializer:
                return StringTemplate(initializer);
            case IPropertyReferenceOperation { Property: { IsStatic: true, IsIndexer: false } property } when PropertyInitializer(property) is { } propertyInitializer:
                // A settable static property (for example a table prefix) has its initializer as the likely value, never a certain one.
                return (StringTemplate(propertyInitializer).Template, false);
            case IObjectCreationOperation { Constructor.ContainingType: { } uriType } creation when FullName(uriType) == "System.Uri" && creation.Arguments.Length > 0:
                return StringTemplate(creation.Arguments[0].Value);
            default:
                return (null, false);
        }
    }

    /// <summary>The initializer of an auto-property declared in source ("{ get; set; } = value").</summary>
    private IOperation? PropertyInitializer(IPropertySymbol property)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is PropertyDeclarationSyntax { Initializer.Value: { } value }
                && Compilation.ContainsSyntaxTree(value.SyntaxTree))
            {
                return Compilation.GetSemanticModel(value.SyntaxTree).GetOperation(value) is { } op ? Unwrap(op) : null;
            }
        }
        return null;
    }

    /// <summary>The initializer of a readonly (or const) field that is never assigned outside its declaration and constructors.</summary>
    private IOperation? FieldInitializer(IFieldSymbol field)
    {
        if (!field.IsReadOnly && !field.IsConst)
        {
            return null;
        }
        foreach (var reference in field.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } value } && Compilation.ContainsSyntaxTree(value.SyntaxTree))
            {
                var assignedInConstructor = field.ContainingType.InstanceConstructors.Concat(field.ContainingType.StaticConstructors)
                    .SelectMany(c => c.DeclaringSyntaxReferences).Select(r => r.GetSyntax())
                    .SelectMany(n => n.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    .Any(a => a.Left.ToString().EndsWith(field.Name, StringComparison.Ordinal));
                if (assignedInConstructor)
                {
                    return null;
                }
                var model = Compilation.GetSemanticModel(value.SyntaxTree);
                return model.GetOperation(value) is { } op ? Unwrap(op) : null;
            }
        }
        return null;
    }

    /// <summary>The value assigned to a property inside a configuration lambda, for example o =&gt; o.BaseAddress = new("http://x").</summary>
    public (string? Template, bool Constant, SyntaxNode? Node) AssignedInLambda(IOperation? lambda, string propertyName)
    {
        var (template, constant, node, _) = AssignedValueInLambda(lambda, propertyName);
        return (template, constant, node);
    }

    /// <summary>As <see cref="AssignedInLambda"/>, plus the assigned value operation.</summary>
    public (string? Template, bool Constant, SyntaxNode? Node, IOperation? Value) AssignedValueInLambda(IOperation? lambda, string propertyName)
    {
        if (lambda is IDelegateCreationOperation delegateCreation)
        {
            lambda = delegateCreation.Target;
        }
        if (lambda is not IAnonymousFunctionOperation function)
        {
            return (null, false, null, null);
        }
        foreach (var assignment in function.Body.Descendants().OfType<ISimpleAssignmentOperation>())
        {
            if (Unwrap(assignment.Target) is IPropertyReferenceOperation property && property.Property.Name == propertyName)
            {
                var (template, constant) = StringTemplate(assignment.Value);
                return (template, constant, assignment.Syntax, assignment.Value);
            }
        }
        return (null, false, null, null);
    }

    public static bool IsConditional(SyntaxNode node) =>
        node.Ancestors().TakeWhile(a => a is not MemberDeclarationSyntax and not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax)
            .Any(a => a is IfStatementSyntax or ConditionalExpressionSyntax or SwitchStatementSyntax or SwitchExpressionSyntax or ElseClauseSyntax
                or ConditionalAccessExpressionSyntax or BinaryExpressionSyntax { RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.CoalesceExpression });

    /// <summary>Entity id of the member that contains a node (lambdas and local functions belong to their container).</summary>
    public string? EnclosingMember(SyntaxNode node, SemanticModel model)
    {
        foreach (var ancestor in node.Ancestors())
        {
            ISymbol? symbol = ancestor switch
            {
                GlobalStatementSyntax global => model.GetEnclosingSymbol(global.SpanStart),
                AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax => null,
                VariableDeclaratorSyntax v when v.Parent?.Parent is BaseFieldDeclarationSyntax => model.GetDeclaredSymbol(v),
                BaseFieldDeclarationSyntax or BaseNamespaceDeclarationSyntax => null,
                MemberDeclarationSyntax member => model.GetDeclaredSymbol(member),
                _ => null,
            };
            if (symbol is not null)
            {
                return IdOf(symbol);
            }
        }
        return null;
    }
}
