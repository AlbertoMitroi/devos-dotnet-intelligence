using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace DotnetIntelligence.Analysis.Extraction;

/// <summary>
/// Logical entity ids (3.3): dotnet:{projectPath}:{documentationCommentId} for source symbols and
/// dotnet:pkg:{packageId}@{version}:{assemblyName}:{documentationCommentId} for metadata symbols.
/// Symbols without a documentation comment id (locals, lambdas, local functions, anonymous types) have no id (D6).
/// </summary>
public sealed class SymbolIdentity
{
    private readonly IReadOnlyDictionary<string, string> _projectByAssembly;
    private readonly string _nugetRoot;
    private readonly IReadOnlyDictionary<string, string> _packageCasing;
    private readonly ConcurrentDictionary<string, (string Id, string Version)?> _packageByReference = new(StringComparer.Ordinal);

    /// <param name="projectByAssembly">Assembly name to repository-relative project path, for every loaded project.</param>
    /// <param name="packageCasing">Lower-case package id to its declared casing, from the assets files.</param>
    public SymbolIdentity(IReadOnlyDictionary<string, string> projectByAssembly, string nugetRoot, IReadOnlyDictionary<string, string> packageCasing)
    {
        _projectByAssembly = projectByAssembly;
        _nugetRoot = Path.GetFullPath(nugetRoot).TrimEnd('/') + "/";
        _packageCasing = packageCasing;
    }

    /// <summary>Canonical symbol for identity: definitions instead of constructed generics and reduced extension methods.</summary>
    public static ISymbol? Normalize(ISymbol? symbol)
    {
        // Members of anonymous types share documentation ids such as "P:.Id" across unrelated types: out of scope (D6).
        for (var container = symbol?.ContainingType; container is not null; container = container.ContainingType)
        {
            if (container.IsAnonymousType)
            {
                return null;
            }
        }
        switch (symbol)
        {
            case null:
                return null;
            case IAliasSymbol alias:
                return Normalize(alias.Target);
            case IMethodSymbol method:
                var m = method.ReducedFrom ?? method;
                if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
                    && m.AssociatedSymbol is { } associated)
                {
                    return associated.OriginalDefinition;
                }
                if (m.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction or MethodKind.LambdaMethod)
                {
                    return null;
                }
                return m.OriginalDefinition;
            case IFieldSymbol field when field.ContainingType?.IsTupleType == true:
                return null;
            case INamedTypeSymbol type when type.IsTupleType || type.IsAnonymousType:
                return null;
            case INamedTypeSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamespaceSymbol:
                return symbol.OriginalDefinition;
            default:
                return null;
        }
    }

    public string? ProjectOf(ISymbol symbol) =>
        symbol.ContainingAssembly is { } assembly && _projectByAssembly.TryGetValue(assembly.Name, out var project) ? project : null;

    /// <summary>The entity id, or null for symbols out of scope.</summary>
    public string? IdOf(ISymbol symbol, Compilation compilation)
    {
        if (symbol is INamespaceSymbol { IsGlobalNamespace: true } || symbol.Kind == SymbolKind.ErrorType)
        {
            return null;
        }
        var docId = symbol.GetDocumentationCommentId();
        if (docId is null)
        {
            return null;
        }
        if (symbol is INamespaceSymbol)
        {
            // Namespaces span assemblies; they are identified per project when declared in source, else per package.
            return null;
        }
        var project = ProjectOf(symbol);
        if (project is not null && symbol.Locations.Any(l => l.IsInSource))
        {
            return $"dotnet:{project}:{docId}";
        }
        var assembly = symbol.ContainingAssembly;
        if (assembly is null)
        {
            return null;
        }
        var package = PackageOf(assembly, compilation);
        return package is { } p
            ? $"dotnet:pkg:{p.Id}@{p.Version}:{assembly.Name}:{docId}"
            : $"dotnet:pkg:unknown@unknown:{assembly.Name}:{docId}";
    }

    /// <summary>Namespace id within a project (namespaces declared in source).</summary>
    public static string NamespaceId(string project, INamespaceSymbol ns) => $"dotnet:{project}:{ns.GetDocumentationCommentId()}";

    /// <summary>Package of a metadata assembly: from its path in the NuGet folder or in the SDK's targeting packs.</summary>
    public (string Id, string Version)? PackageOf(IAssemblySymbol assembly, Compilation compilation)
    {
        if (compilation.GetMetadataReference(assembly) is not PortableExecutableReference { FilePath: { } path })
        {
            return null;
        }
        return _packageByReference.GetOrAdd(path, p =>
        {
            var full = Path.GetFullPath(p).Replace('\\', '/');
            if (full.StartsWith(_nugetRoot, StringComparison.Ordinal))
            {
                var parts = full[_nugetRoot.Length..].Split('/');
                if (parts.Length >= 2)
                {
                    return (_packageCasing.GetValueOrDefault(parts[0], parts[0]), parts[1]);
                }
            }
            var packs = full.IndexOf("/packs/", StringComparison.Ordinal);
            if (packs >= 0)
            {
                var parts = full[(packs + "/packs/".Length)..].Split('/');
                if (parts.Length >= 2)
                {
                    return (parts[0], parts[1]);
                }
            }
            return null;
        });
    }

    public static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => t.TypeKind switch
        {
            TypeKind.Class => t.IsRecord ? "record" : "class",
            TypeKind.Struct => t.IsRecord ? "record_struct" : "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            // C# 14 extension blocks are grouped in compiler-generated extension types.
            _ when t.TypeKind.ToString() == "Extension" => "extension",
            _ => "type",
        },
        IMethodSymbol m => m.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => "constructor",
            MethodKind.Destructor => "destructor",
            MethodKind.UserDefinedOperator or MethodKind.BuiltinOperator => "operator",
            MethodKind.Conversion => "conversion",
            _ => "method",
        },
        IPropertySymbol p => p.IsIndexer ? "indexer" : "property",
        IFieldSymbol f => f.ContainingType?.TypeKind == TypeKind.Enum ? "enum_member" : f.IsConst ? "constant" : "field",
        IEventSymbol => "event",
        INamespaceSymbol => "namespace",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    public static IReadOnlyList<string> ModifiersOf(ISymbol symbol)
    {
        var modifiers = new List<string>();
        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsAbstract && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Interface }) modifiers.Add("abstract");
        if (symbol.IsSealed && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Enum or TypeKind.Delegate }) modifiers.Add("sealed");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");
        if (symbol.IsExtern) modifiers.Add("extern");
        switch (symbol)
        {
            case IMethodSymbol { IsAsync: true }:
                modifiers.Add("async");
                break;
            case IFieldSymbol { IsReadOnly: true }:
                modifiers.Add("readonly");
                break;
            case IFieldSymbol { IsConst: true }:
                modifiers.Add("const");
                break;
            case INamedTypeSymbol { IsReadOnly: true }:
                modifiers.Add("readonly");
                break;
            case IPropertySymbol { IsRequired: true }:
                modifiers.Add("required");
                break;
        }
        if (symbol.DeclaringSyntaxReferences.Length > 1 || symbol.DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax member
                && member.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)))
        {
            modifiers.Add("partial");
        }
        return modifiers;
    }

    public static string AccessibilityOf(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected_internal",
        Accessibility.ProtectedAndInternal => "private_protected",
        Accessibility.Private => "private",
        _ => "not_applicable",
    };

    public static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeRef,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeOptionalBrackets,
        propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
}
