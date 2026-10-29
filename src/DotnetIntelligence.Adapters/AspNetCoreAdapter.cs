using DotnetIntelligence.Contracts;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// ASP.NET Core endpoints (4.3): minimal APIs (Map*, MapMethods, MapGroup prefixes through locals and fluent chains,
/// constants folded) and controllers (attribute routes; conventional routes from MapControllerRoute patterns).
/// API versions from HasApiVersion go into ext.dotnet.apiVersions.
/// </summary>
public sealed class AspNetCoreAdapter(AdapterContext context)
{
    private const string EndpointExtensions = "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions";
    private readonly List<(string Pattern, string? Evidence)> _conventionalRoutes = [];

    private static readonly Dictionary<string, string> MapMethods = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH", ["Map"] = "*",
    };

    public void VisitInvocation(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var type = AdapterContext.FullName(method.ContainingType);
        if (type == "Microsoft.AspNetCore.Builder.ControllerEndpointRouteBuilderExtensions" && method.Name is "MapControllerRoute" or "MapDefaultControllerRoute")
        {
            var pattern = method.Name == "MapDefaultControllerRoute" ? "{controller=Home}/{action=Index}/{id?}"
                : context.StringTemplate(AdapterContext.Argument(invocation, "pattern")).Template;
            if (pattern is not null)
            {
                _conventionalRoutes.Add((pattern, context.Evidence(invocation.Syntax)));
            }
            return;
        }
        if (type != EndpointExtensions || !(MapMethods.ContainsKey(method.Name) || method.Name == "MapMethods"))
        {
            return;
        }
        var (pattern2, constant) = context.StringTemplate(AdapterContext.Argument(invocation, "pattern"));
        if (pattern2 is null)
        {
            return;
        }
        var methods = method.Name == "MapMethods"
            ? ConstantStrings(AdapterContext.Argument(invocation, "httpMethods"))
            : [MapMethods[method.Name]];
        var (prefixes, prefixResolved, versions, groupEvidence) = Prefixes(AdapterContext.Receiver(invocation), 0);
        prefixes.Reverse();
        var template = Routes.Combine([.. prefixes, pattern2]);
        var handler = AdapterContext.Argument(invocation, "handler") switch
        {
            IDelegateCreationOperation { Target: IMethodReferenceOperation reference } => context.IdOf(reference.Method),
            _ => null,
        };
        foreach (var httpMethod in methods.Count == 0 ? ["{}"] : methods)
        {
            var fields = new JsonObject
            {
                ["method"] = httpMethod,
                ["routeTemplate"] = template,
                ["canonicalRoute"] = Routes.Canonical(template),
                ["handler"] = handler,
                ["resolution"] = constant && prefixResolved ? "exact" : "candidate",
                ["ext.dotnet.style"] = "minimal",
            };
            if (versions.Count > 0)
            {
                fields["ext.dotnet.apiVersions"] = new JsonArray(versions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(v => (JsonNode)v).ToArray());
            }
            if (!prefixResolved)
            {
                fields["ext.dotnet.prefixUnresolved"] = true;
            }
            context.Observe("http.endpoint", fields, [context.Evidence(invocation.Syntax), .. groupEvidence]);
        }
    }

    /// <summary>MapGroup prefixes from the receiver chain, innermost first; false when a part could not be followed.</summary>
    private (List<string> Prefixes, bool Resolved, List<string> Versions, List<string?> Evidence) Prefixes(IOperation? receiver, int depth)
    {
        var prefixes = new List<string>();
        var versions = new List<string>();
        var evidence = new List<string?>();
        var resolved = true;
        var current = receiver;
        while (current is not null && depth++ < 32)
        {
            switch (current)
            {
                case IInvocationOperation call:
                    var target = call.TargetMethod.ReducedFrom ?? call.TargetMethod;
                    if (target.Name == "MapGroup" && AdapterContext.FullName(target.ContainingType) == EndpointExtensions)
                    {
                        var (prefix, constant) = context.StringTemplate(AdapterContext.Argument(call, "prefix"));
                        prefixes.Add(prefix ?? "{}");
                        resolved &= constant;
                        evidence.Add(context.Evidence(call.Syntax));
                    }
                    else if (target.Name == "HasApiVersion")
                    {
                        versions.Add(string.Join('.', call.Arguments.Skip(target.IsExtensionMethod ? 1 : 0)
                            .Select(a => AdapterContext.Unwrap(a.Value).ConstantValue).Where(v => v.HasValue).Select(v => Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture))));
                    }
                    current = AdapterContext.Receiver(call);
                    continue;
                case ILocalReferenceOperation local:
                    current = context.LocalValue(local.Local);
                    if (current is null)
                    {
                        resolved = false;
                    }
                    continue;
                case IParameterReferenceOperation or IInstanceReferenceOperation:
                    // The builder comes from the caller; a prefix applied there is not followed (ext.dotnet.prefixUnresolved is not set for it).
                    current = null;
                    continue;
                case IPropertyReferenceOperation or IFieldReferenceOperation:
                    current = null;
                    continue;
                default:
                    resolved = false;
                    current = null;
                    continue;
            }
        }
        return (prefixes, resolved, versions, evidence);
    }

    private static List<string> ConstantStrings(IOperation? operation) => operation switch
    {
        IArrayCreationOperation { Initializer: { } init } => init.ElementValues.Select(v => AdapterContext.Unwrap(v).ConstantValue).Where(c => c is { HasValue: true, Value: string })
            .Select(c => ((string)c.Value!).ToUpperInvariant()).ToList(),
        ICollectionExpressionOperation collection => collection.Elements.Select(v => AdapterContext.Unwrap(v).ConstantValue).Where(c => c is { HasValue: true, Value: string })
            .Select(c => ((string)c.Value!).ToUpperInvariant()).ToList(),
        _ => [],
    };

    // ---- controllers ----

    private static readonly Dictionary<string, string> HttpAttributes = new(StringComparer.Ordinal)
    {
        ["Microsoft.AspNetCore.Mvc.HttpGetAttribute"] = "GET",
        ["Microsoft.AspNetCore.Mvc.HttpPostAttribute"] = "POST",
        ["Microsoft.AspNetCore.Mvc.HttpPutAttribute"] = "PUT",
        ["Microsoft.AspNetCore.Mvc.HttpDeleteAttribute"] = "DELETE",
        ["Microsoft.AspNetCore.Mvc.HttpPatchAttribute"] = "PATCH",
        ["Microsoft.AspNetCore.Mvc.HttpHeadAttribute"] = "HEAD",
        ["Microsoft.AspNetCore.Mvc.HttpOptionsAttribute"] = "OPTIONS",
    };

    private readonly List<INamedTypeSymbol> _conventionalControllers = [];

    public void VisitType(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || !AdapterContext.DerivesFrom(type, "Microsoft.AspNetCore.Mvc.ControllerBase")
            && !type.GetAttributes().Any(a => AdapterContext.FullName(a.AttributeClass) == "Microsoft.AspNetCore.Mvc.ApiControllerAttribute"))
        {
            return;
        }
        var controller = type.Name.EndsWith("Controller", StringComparison.Ordinal) ? type.Name[..^"Controller".Length] : type.Name;
        var classRoutes = RouteTemplates(type).Select(t => t.Template.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)).ToList();
        var anyAttributeRoute = classRoutes.Count > 0;
        foreach (var action in Actions(type))
        {
            var routes = RouteTemplates(action).ToList();
            var verbs = action.GetAttributes().Select(a => (Verb: HttpAttributes.GetValueOrDefault(AdapterContext.FullName(a.AttributeClass)), Attribute: a))
                .Where(v => v.Verb is not null).ToList();
            anyAttributeRoute |= routes.Count > 0;
            if (!anyAttributeRoute && classRoutes.Count == 0)
            {
                _conventionalControllers.Add(type);
                break;
            }
            var actionTemplates = routes.Select(r => r.Template).ToList();
            if (actionTemplates.Count == 0)
            {
                actionTemplates.Add(string.Empty);
            }
            foreach (var classRoute in classRoutes.Count == 0 ? [string.Empty] : classRoutes)
            {
                foreach (var actionTemplate in actionTemplates)
                {
                    var template = actionTemplate.StartsWith('/') || actionTemplate.StartsWith("~/", StringComparison.Ordinal)
                        ? Routes.Combine(actionTemplate.TrimStart('~'))
                        : Routes.Combine(classRoute, actionTemplate);
                    template = template.Replace("[action]", action.Name, StringComparison.OrdinalIgnoreCase);
                    foreach (var verb in verbs.Count == 0 ? [("*", (AttributeData?)null)] : verbs.Select(v => (v.Verb!, (AttributeData?)v.Attribute)))
                    {
                        Endpoint(verb.Item1, template, action, "attribute", "exact", verb.Item2);
                    }
                }
            }
        }
    }

    /// <summary>Conventional routes, after all types and route registrations of the project were visited.</summary>
    public void Complete()
    {
        foreach (var type in _conventionalControllers.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            var controller = type.Name.EndsWith("Controller", StringComparison.Ordinal) ? type.Name[..^"Controller".Length] : type.Name;
            foreach (var (pattern, evidence) in _conventionalRoutes)
            {
                foreach (var action in Actions(type))
                {
                    var template = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{controller(=[^}]*)?\}", controller);
                    template = System.Text.RegularExpressions.Regex.Replace(template, @"\{action(=[^}]*)?\}", action.Name);
                    var verbs = action.GetAttributes().Select(a => HttpAttributes.GetValueOrDefault(AdapterContext.FullName(a.AttributeClass))).Where(v => v is not null).ToList();
                    foreach (var verb in verbs.Count == 0 ? ["*"] : verbs)
                    {
                        Endpoint(verb!, Routes.Combine(template), action, "conventional", "candidate", null, evidence);
                    }
                }
            }
        }
    }

    private void Endpoint(string verb, string template, IMethodSymbol action, string style, string resolution, AttributeData? attribute, string? extraEvidence = null)
    {
        var location = attribute?.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? action.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null)
        {
            return;
        }
        context.Observe("http.endpoint", new JsonObject
        {
            ["method"] = verb,
            ["routeTemplate"] = template,
            ["canonicalRoute"] = Routes.Canonical(template),
            ["handler"] = context.IdOf(action),
            ["resolution"] = resolution,
            ["ext.dotnet.style"] = style,
        }, context.Evidence(location), extraEvidence);
    }

    private static IEnumerable<IMethodSymbol> Actions(INamedTypeSymbol type) =>
        type.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public
            && !m.IsStatic && !m.GetAttributes().Any(a => AdapterContext.FullName(a.AttributeClass) == "Microsoft.AspNetCore.Mvc.NonActionAttribute"));

    private static IEnumerable<(string Template, AttributeData Attribute)> RouteTemplates(ISymbol symbol) =>
        symbol.GetAttributes()
            .Where(a => AdapterContext.FullName(a.AttributeClass) == "Microsoft.AspNetCore.Mvc.RouteAttribute" || HttpAttributes.ContainsKey(AdapterContext.FullName(a.AttributeClass)))
            .Where(a => a.ConstructorArguments.FirstOrDefault().Value is string)
            .Select(a => ((string)a.ConstructorArguments[0].Value!, a));
}
