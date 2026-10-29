using DotnetIntelligence.Contracts;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// HTTP clients (4.3): HttpClient and System.Net.Http.Json calls become http.call with a URL template (constants
/// folded, interpolation holes as "{}"). The client is the type that holds the HttpClient (typed client); its base
/// address comes from the AddHttpClient registration and is resolved while linking. Unresolvable URLs are kept as
/// unresolved with the reason.
/// </summary>
public sealed class HttpClientAdapter(AdapterContext context)
{
    private static readonly Dictionary<string, string> Verbs = new(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET", ["GetStringAsync"] = "GET", ["GetStreamAsync"] = "GET", ["GetByteArrayAsync"] = "GET",
        ["GetFromJsonAsync"] = "GET", ["GetFromJsonAsAsyncEnumerable"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST",
        ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT",
        ["PatchAsync"] = "PATCH", ["PatchAsJsonAsync"] = "PATCH",
        ["DeleteAsync"] = "DELETE", ["DeleteFromJsonAsync"] = "DELETE",
        ["SendAsync"] = "{}",
    };

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var type = AdapterContext.FullName(method.ContainingType);
        if (type is not ("System.Net.Http.HttpClient" or "System.Net.Http.Json.HttpClientJsonExtensions") || !Verbs.TryGetValue(method.Name, out var verb))
        {
            return;
        }
        IOperation? uri = AdapterContext.Argument(invocation, "requestUri");
        if (method.Name == "SendAsync" && AdapterContext.Argument(invocation, "request") is { } request)
        {
            (verb, uri) = FromRequestMessage(request);
        }
        var (template, constant) = context.StringTemplate(uri);
        var receiver = AdapterContext.Receiver(invocation);
        var client = receiver switch
        {
            IFieldReferenceOperation field => field.Field.ContainingType,
            IPropertyReferenceOperation property => property.Property.ContainingType,
            IParameterReferenceOperation parameter => parameter.Parameter.ContainingSymbol?.ContainingType,
            _ => null,
        };
        var fields = new JsonObject
        {
            ["method"] = verb,
            ["urlTemplate"] = template,
            ["canonicalRoute"] = template is null ? null : Routes.Canonical(template),
            ["client"] = context.IdOf(client),
            ["caller"] = context.EnclosingMember(invocation.Syntax, model),
            // Holes only in the query string do not change which endpoint is called.
            ["resolution"] = template is null ? "unresolved" : constant || !(Routes.QueryStart(template) is var q && q >= 0 ? template[..q] : template).Contains("{}", StringComparison.Ordinal) ? "exact" : "candidate",
        };
        if (template is null)
        {
            fields["reason"] = uri is null ? "The request URI is not an argument of the call." : $"The request URI is computed ({AdapterContext.Unwrap(uri).Kind}) and could not be folded to a template.";
        }
        context.Observe("http.call", fields, context.Evidence(invocation.Syntax));
    }

    private (string Verb, IOperation? Uri) FromRequestMessage(IOperation request)
    {
        if (request is ILocalReferenceOperation local)
        {
            request = context.LocalValue(local.Local) ?? request;
        }
        if (request is IObjectCreationOperation creation && AdapterContext.FullName(creation.Type) == "System.Net.Http.HttpRequestMessage" && creation.Arguments.Length >= 2)
        {
            var verb = AdapterContext.Unwrap(creation.Arguments[0].Value) is IPropertyReferenceOperation property
                && AdapterContext.FullName(property.Property.ContainingType) == "System.Net.Http.HttpMethod" ? property.Property.Name.ToUpperInvariant() : "{}";
            return (verb, creation.Arguments[1].Value);
        }
        return ("{}", null);
    }
}
