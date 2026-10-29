using Billing;
using Contracts;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
var kafka = builder.Configuration.GetSection("Kafka").Get<KafkaOptions>()!;

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderSubmittedConsumer>();
    x.AddConsumer<SubmitInvoiceConsumer>();
    if (builder.Configuration["Bus:Transport"] == "AzureServiceBus")
    {
        x.UsingAzureServiceBus((context, cfg) => cfg.ConfigureEndpoints(context));
    }
    else
    {
        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.ReceiveEndpoint("billing-orders", e => e.ConfigureConsumer<OrderSubmittedConsumer>(context));
            cfg.ConfigureEndpoints(context);
        });
    }
    x.AddRider(rider =>
    {
        rider.AddConsumer<ApplicationReceivedConsumer>();
        rider.AddProducer<ApplicationReceived>(kafka.Topics.Applications);
        rider.UsingKafka((context, k) =>
        {
            k.Host(builder.Configuration["Kafka:Host"]);
            k.TopicEndpoint<ApplicationReceived>(builder.Configuration["Kafka:Topics:Applications"], "billing",
                e => e.ConfigureConsumer<ApplicationReceivedConsumer>(context));
        });
    });
});

builder.Build().Run();
