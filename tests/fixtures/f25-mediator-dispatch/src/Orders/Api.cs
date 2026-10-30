using MediatR;

namespace Orders;

public sealed class Api(ISender sender)
{
    public Task<bool> Place() => sender.Send(new PlaceOrder(1));

    public Task<string> WrapInvoice() => sender.Send(new Envelope<Invoice>(new Invoice(1)));

    public Task<string> WrapReceipt() => sender.Send(new Envelope<Receipt>(new Receipt(1)));

    public Task Audit() => sender.Send(new AuditLogin("lab"));

    public Task PingAll() => sender.Send(new Ping());

    public Task Nobody() => sender.Send(new Unhandled());

    public Task<bool> Dynamic(IRequest<bool> request) => sender.Send(request);
}
