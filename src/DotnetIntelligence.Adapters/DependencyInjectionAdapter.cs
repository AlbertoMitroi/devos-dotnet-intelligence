using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// Dependency injection (4.3): registrations by resolved Microsoft.Extensions.DependencyInjection symbols
/// (Add*, TryAdd*, AddKeyed*, AddHostedService, AddHttpClient, AddGrpcClient) with lifetime, key, conditional flag,
/// the member that registers and the call path from its composition root; consumers through constructors,
/// [FromServices], [FromKeyedServices], [Inject] and explicit GetService/GetRequiredService.
/// </summary>
public sealed class DependencyInjectionAdapter(AdapterContext context, CallGraph calls)
{
    private const string Collection = "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions";
    private const string Descriptors = "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions";
    private const string Hosted = "Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions";
    private const string HttpClients = "Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions";
    private const string GrpcClients = "Microsoft.Extensions.DependencyInjection.GrpcClientServiceExtensions";

    public void VisitInvocation(IInvocationOperation invocation, SemanticModel model)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var type = AdapterContext.FullName(method.ContainingType);
        if (type is Collection or Descriptors && (method.Name.StartsWith("Add", StringComparison.Ordinal) || method.Name.StartsWith("TryAdd", StringComparison.Ordinal))
            && Lifetime(method.Name) is { } lifetime)
        {
            Registration(invocation, model, lifetime, keyed: method.Name.Contains("Keyed", StringComparison.Ordinal), kind: "service");
        }
        else if (type == Hosted && method.Name == "AddHostedService")
        {
            Registration(invocation, model, "singleton", keyed: false, kind: "hostedService");
        }
        else if (type == HttpClients && method.Name == "AddHttpClient" && invocation.TargetMethod.TypeArguments.Length > 0)
        {
            Registration(invocation, model, "transient", keyed: false, kind: "typedHttpClient");
        }
        else if (type == GrpcClients && method.Name == "AddGrpcClient" && invocation.TargetMethod.TypeArguments.Length > 0)
        {
            Registration(invocation, model, "transient", keyed: false, kind: "grpcClient");
        }
        else if (IsServiceLookup(invocation.TargetMethod) is { } via)
        {
            var service = invocation.TargetMethod.TypeArguments.FirstOrDefault()
                ?? (AdapterContext.Argument(invocation, "serviceType") is ITypeOfOperation typeOf ? typeOf.TypeOperand : null);
            if (context.IdOf(service) is { } serviceId && context.EnclosingMember(invocation.Syntax, model) is { } consumer)
            {
                context.Observe("di.consumes", new JsonObject
                {
                    ["service"] = serviceId,
                    ["consumer"] = consumer,
                    ["via"] = via,
                    ["key"] = KeyOf(AdapterContext.Argument(invocation, "serviceKey")),
                }, context.Evidence(invocation.Syntax));
            }
        }
    }

    private static string? IsServiceLookup(IMethodSymbol method)
    {
        var type = AdapterContext.FullName(method.ContainingType);
        return method.Name switch
        {
            "GetService" or "GetRequiredService" when type is "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions" or "System.IServiceProvider" => method.Name == "GetService" ? "getService" : "getRequiredService",
            "GetKeyedService" or "GetRequiredKeyedService" when type is "Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions" => method.Name == "GetKeyedService" ? "getKeyedService" : "getRequiredKeyedService",
            _ => null,
        };
    }

    private static string? Lifetime(string name) =>
        name.Contains("Singleton", StringComparison.Ordinal) ? "singleton"
        : name.Contains("Scoped", StringComparison.Ordinal) ? "scoped"
        : name.Contains("Transient", StringComparison.Ordinal) ? "transient"
        : null;

    private void Registration(IInvocationOperation invocation, SemanticModel model, string lifetime, bool keyed, string kind)
    {
        var target = invocation.TargetMethod;
        ITypeSymbol? service = null;
        ITypeSymbol? implementation = null;
        var factory = false;
        var typeArguments = target.TypeArguments;
        var typeParameters = target.OriginalDefinition.TypeParameters;
        for (var i = 0; i < typeArguments.Length; i++)
        {
            switch (typeParameters[i].Name)
            {
                case "TService" or "TClient" or "THostedService":
                    service = typeArguments[i];
                    break;
                case "TImplementation":
                    implementation = typeArguments[i];
                    break;
            }
        }
        if (AdapterContext.Argument(invocation, "serviceType") is ITypeOfOperation serviceType)
        {
            service = serviceType.TypeOperand;
        }
        if (AdapterContext.Argument(invocation, "implementationType") is ITypeOfOperation implementationType)
        {
            implementation = implementationType.TypeOperand;
        }
        if (AdapterContext.Argument(invocation, "implementationInstance") is { Type: { } instanceType })
        {
            implementation = instanceType;
        }
        if (AdapterContext.Argument(invocation, "implementationFactory") is { } factoryOperation)
        {
            factory = true;
            implementation ??= FactoryReturnType(factoryOperation);
        }
        if (kind == "hostedService")
        {
            implementation = service;
            service = context.Compilation.GetTypeByMetadataName("Microsoft.Extensions.Hosting.IHostedService");
        }
        implementation ??= factory ? null : service;
        var serviceId = context.IdOf(service);
        var registeredIn = context.EnclosingMember(invocation.Syntax, model);
        if (serviceId is null || registeredIn is null)
        {
            return;
        }
        var path = calls.PathFromRoot(registeredIn);
        var fields = new JsonObject
        {
            ["service"] = serviceId,
            ["implementation"] = context.IdOf(implementation),
            ["factory"] = factory,
            ["lifetime"] = lifetime,
            ["key"] = keyed ? KeyOf(AdapterContext.Argument(invocation, "serviceKey")) : null,
            ["conditional"] = AdapterContext.IsConditional(invocation.Syntax),
            ["registeredIn"] = registeredIn,
            ["compositionRoot"] = path[0],
            ["registrationPath"] = new JsonArray(path.Select(p => (JsonNode)p).ToArray()),
            ["ext.dotnet.registration"] = kind,
        };
        if (kind == "typedHttpClient")
        {
            var (address, constant, _) = context.AssignedInLambda(AdapterContext.Argument(invocation, "configureClient"), "BaseAddress");
            fields["baseAddress"] = address;
            fields["baseAddressResolved"] = address is not null && constant;
        }
        else if (kind == "grpcClient")
        {
            var (address, constant, _) = context.AssignedInLambda(AdapterContext.Argument(invocation, "configureClient"), "Address");
            fields["baseAddress"] = address;
            fields["baseAddressResolved"] = address is not null && constant;
        }
        context.Observe("di.registers", fields, context.Evidence(invocation.Syntax));
    }

    private static ITypeSymbol? FactoryReturnType(IOperation factory)
    {
        if (factory is IDelegateCreationOperation creation)
        {
            factory = creation.Target;
        }
        if (factory is IAnonymousFunctionOperation lambda)
        {
            var returned = lambda.Body.Descendants().OfType<IReturnOperation>().Select(r => r.ReturnedValue).Where(v => v is not null)
                .Select(v => AdapterContext.Unwrap(v!).Type).Where(t => t is not null).Distinct(SymbolEqualityComparer.Default).ToList();
            return returned.Count == 1 ? (ITypeSymbol)returned[0]! : null;
        }
        return null;
    }

    private static string? KeyOf(IOperation? key) => key?.ConstantValue is { HasValue: true } value ? value.Value?.ToString() : key is null ? null : "{}";

    /// <summary>Consumers declared by types: constructor and primary-constructor parameters, [FromServices], [FromKeyedServices], [Inject].</summary>
    public void VisitType(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsRecord || type.IsStatic || type.IsAbstract && !type.InstanceConstructors.Any(c => c.Parameters.Length > 0))
        {
            return;
        }
        foreach (var constructor in type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared || c.Parameters.Length > 0))
        {
            var primary = constructor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax);
            foreach (var parameter in constructor.Parameters)
            {
                Consume(parameter, constructor, primary ? "primaryConstructor" : "constructor");
            }
        }
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
        {
            foreach (var parameter in method.Parameters.Where(p => Attribute(p, "Microsoft.AspNetCore.Mvc.FromServicesAttribute") is not null
                         || Attribute(p, "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute") is not null))
            {
                Consume(parameter, method, Attribute(parameter, "Microsoft.AspNetCore.Mvc.FromServicesAttribute") is not null ? "fromServices" : "fromKeyedServices");
            }
        }
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>()
                     .Where(p => Attribute(p, "Microsoft.AspNetCore.Components.InjectAttribute") is not null))
        {
            if (context.IdOf(property.Type) is { } serviceId && context.IdOf(property) is { } consumer
                && property.Locations.FirstOrDefault(l => l.IsInSource) is { } location)
            {
                context.Observe("di.consumes", new JsonObject { ["service"] = serviceId, ["consumer"] = consumer, ["via"] = "inject", ["key"] = null },
                    context.Evidence(location));
            }
        }
    }

    private void Consume(IParameterSymbol parameter, IMethodSymbol member, string via)
    {
        if (parameter.Type is not INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class } serviceType
            || serviceType.SpecialType != SpecialType.None || serviceType.IsRecord
            || context.IdOf(serviceType) is not { } serviceId || context.IdOf(member) is not { } consumer
            || parameter.Locations.FirstOrDefault(l => l.IsInSource) is not { } location)
        {
            return;
        }
        var keyed = Attribute(parameter, "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute");
        context.Observe("di.consumes", new JsonObject
        {
            ["service"] = serviceId,
            ["consumer"] = consumer,
            ["via"] = keyed is not null ? "fromKeyedServices" : via,
            ["key"] = keyed?.ConstructorArguments.FirstOrDefault().Value?.ToString(),
        }, context.Evidence(location));
    }

    private static AttributeData? Attribute(ISymbol symbol, string fullName) =>
        symbol.GetAttributes().FirstOrDefault(a => AdapterContext.FullName(a.AttributeClass) == fullName);
}
