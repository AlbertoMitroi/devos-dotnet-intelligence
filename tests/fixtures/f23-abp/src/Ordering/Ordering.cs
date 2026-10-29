using Volo.Abp.Application.Services;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.EventBus.RabbitMq;
using Volo.Abp.Modularity;

namespace Shop.Ordering;

public class OrderAppService(IDistributedEventBus eventBus) : ApplicationService, IOrderAppService
{
    public Task<OrderDto> GetAsync(Guid id) => Task.FromResult(new OrderDto(id));

    public async Task CreateAsync(CreateOrderDto input)
    {
        await eventBus.PublishAsync(new OrderPlacedEto { OrderId = Guid.NewGuid() });
    }
}

[DependsOn(typeof(AbpEventBusRabbitMqModule))]
public class OrderingModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var options = new AbpAspNetCoreMvcOptions();
        options.ConventionalControllers.Create(typeof(OrderingModule).Assembly, o => o.RootPath = "ordering");
    }
}
