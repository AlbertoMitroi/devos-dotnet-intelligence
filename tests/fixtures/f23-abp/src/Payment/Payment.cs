using Shop.Ordering;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.EventBus.RabbitMq;
using Volo.Abp.Modularity;

namespace Shop.Payment;

public class OrderPlacedHandler(IOrderAppService orders) : IDistributedEventHandler<OrderPlacedEto>
{
    public async Task HandleEventAsync(OrderPlacedEto eventData) => await orders.GetAsync(eventData.OrderId);
}

[DependsOn(typeof(AbpEventBusRabbitMqModule))]
public class PaymentModule : AbpModule;
