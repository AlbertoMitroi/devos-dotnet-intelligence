using Bus;

namespace Billing;

public sealed class OrderPlacedHandler : IHandles<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message) => Task.CompletedTask;
}

public static class Setup
{
    public static void Register() => Subscriptions.Subscribe<OrderPlaced, OrderPlacedHandler>();
}
