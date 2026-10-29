using Volo.Abp.Application.Services;
using Volo.Abp.EventBus;

namespace Shop.Ordering;

[EventName("Shop.Order.Placed")]
public class OrderPlacedEto
{
    public Guid OrderId { get; set; }
}

public record OrderDto(Guid Id);

public record CreateOrderDto(string Customer);

public interface IOrderAppService : IApplicationService
{
    Task<OrderDto> GetAsync(Guid id);

    Task CreateAsync(CreateOrderDto input);
}
