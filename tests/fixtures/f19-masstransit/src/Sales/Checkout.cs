using Contracts;
using MassTransit;

namespace Sales;

public class Checkout(IPublishEndpoint publisher, ISendEndpointProvider sender, ITopicProducer<ApplicationReceived> producer)
{
    public async Task CompleteAsync(int orderId)
    {
        await publisher.Publish(new OrderSubmitted(orderId));
        var endpoint = await sender.GetSendEndpoint(new Uri("queue:submit-invoice"));
        await endpoint.Send(new SubmitInvoice(orderId));
        await producer.Produce(new ApplicationReceived(orderId.ToString()));
    }
}
