using Microsoft.AspNetCore.SignalR;

namespace Hubs;

public sealed class FlowHub : Hub
{
}

public interface IHubUtility
{
    Task SendToAll(string method, object payload);
}

public sealed class HubUtility(IHubContext<FlowHub> hub) : IHubUtility
{
    public Task SendToAll(string method, object payload) => hub.Clients.All.SendAsync(method, payload);
}
