using Volo.Abp.Http.Client.ClientProxying;

namespace Shop.Ordering.ClientProxies;

public partial class OrderClientProxy : ClientProxyBase<IOrderAppService>, IOrderAppService
{
    public virtual async Task<OrderDto> GetAsync(Guid id)
    {
        return await RequestAsync<OrderDto>(nameof(GetAsync), new ClientProxyRequestTypeValue
        {
            { typeof(Guid), id }
        });
    }

    public virtual async Task CreateAsync(CreateOrderDto input)
    {
        await RequestAsync(nameof(CreateAsync), new ClientProxyRequestTypeValue
        {
            { typeof(CreateOrderDto), input }
        });
    }
}
