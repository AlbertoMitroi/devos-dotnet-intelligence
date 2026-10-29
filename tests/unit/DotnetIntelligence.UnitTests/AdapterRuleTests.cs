using DotnetIntelligence.Adapters;
using DotnetIntelligence.Artifacts;

namespace DotnetIntelligence.UnitTests;

/// <summary>Naming rules that adapters reproduce from the libraries they read (step 3b).</summary>
public class AdapterRuleTests
{
    [Theory]
    [InlineData("dotnet:src/A/A.csproj:T:Billing.SubmitOrderConsumer", "submit-order")]
    [InlineData("dotnet:src/A/A.csproj:T:Billing.OrderSubmittedConsumer", "order-submitted")]
    [InlineData("dotnet:src/A/A.csproj:T:Billing.Consumer", "consumer")]
    [InlineData("dotnet:src/A/A.csproj:T:Billing.SubmitHTTPRequestConsumer", "submit-http-request")]
    public void MassTransit_kebab_case_endpoint_names(string consumerId, string expected) =>
        Assert.Equal(expected, MassTransitAdapter.KebabEndpointName(consumerId));

    [Theory]
    [InlineData("Order", "order")]
    [InlineData("PublicProduct", "public-product")]
    [InlineData("AuthorLookup", "author-lookup")]
    public void Abp_controller_and_action_names_are_kebab_case(string name, string expected) =>
        Assert.Equal(expected, AbpAdapter.Kebab(name));

    [Theory]
    [InlineData("Send Quote", "SendQuote")]
    [InlineData("send-quote", "SEND_QUOTE")]
    public void Workflow_names_compare_without_case_and_punctuation(string a, string b) =>
        Assert.Equal(Linker.Normalize(a), Linker.Normalize(b));
}
