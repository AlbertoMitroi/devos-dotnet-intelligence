using Bus;

namespace Sales;

public sealed class Checkout(IMessageBus bus)
{
    public Task Complete(int id) => bus.PublishAsync(new OrderPlaced(id));
}
