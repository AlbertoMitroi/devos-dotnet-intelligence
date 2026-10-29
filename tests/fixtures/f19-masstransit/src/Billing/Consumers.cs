using Contracts;
using MassTransit;

namespace Billing;

public class OrderSubmittedConsumer : IConsumer<OrderSubmitted>
{
    public Task Consume(ConsumeContext<OrderSubmitted> context) => Task.CompletedTask;
}

public class SubmitInvoiceConsumer : IConsumer<SubmitInvoice>
{
    public Task Consume(ConsumeContext<SubmitInvoice> context) => Task.CompletedTask;
}

public class ApplicationReceivedConsumer : IConsumer<ApplicationReceived>
{
    public Task Consume(ConsumeContext<ApplicationReceived> context) => Task.CompletedTask;
}

public class KafkaOptions
{
    public TopicOptions Topics { get; set; } = new();
}

public class TopicOptions
{
    public string Applications { get; set; } = "applications-default";
}
