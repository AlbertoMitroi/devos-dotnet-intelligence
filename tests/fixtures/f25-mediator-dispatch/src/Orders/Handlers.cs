using MediatR;

namespace Orders;

public sealed class PlaceOrderHandler : IRequestHandler<PlaceOrder, bool>
{
    public Task<bool> Handle(PlaceOrder request, CancellationToken cancellationToken) => Task.FromResult(request.Id > 0);
}

public sealed class InvoiceEnvelopeHandler : IRequestHandler<Envelope<Invoice>, string>
{
    public Task<string> Handle(Envelope<Invoice> request, CancellationToken cancellationToken) => Task.FromResult("invoice");
}

public sealed class AuditHandler<T> : IRequestHandler<T> where T : AuditCommand
{
    public Task Handle(T request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PingHandlerA : IRequestHandler<Ping>
{
    public Task Handle(Ping request, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PingHandlerB : IRequestHandler<Ping>
{
    public Task Handle(Ping request, CancellationToken cancellationToken) => Task.CompletedTask;
}
