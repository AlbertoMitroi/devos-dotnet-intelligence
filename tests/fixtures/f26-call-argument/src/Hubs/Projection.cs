namespace Hubs;

public sealed class Projection(IHubUtility hub)
{
    private const string FlowUpdated = "flowUpdated";

    public Task Constant() => hub.SendToAll(FlowUpdated, 1);

    public Task Literal() => hub.SendToAll("quoteChanged", 2);

    public Task NameOf() => hub.SendToAll(nameof(Projection), 3);

    public Task Dynamic(string name) => hub.SendToAll(name, 4);

    public Task Secret() => hub.SendToAll("password=hunter2", 5);
}
