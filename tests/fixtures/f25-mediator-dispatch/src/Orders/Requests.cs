using MediatR;

namespace Orders;

public sealed record PlaceOrder(int Id) : IRequest<bool>;

public sealed record Invoice(int Id);

public sealed record Receipt(int Id);

public sealed record Envelope<T>(T Body) : IRequest<string>;

public abstract record AuditCommand : IRequest;

public sealed record AuditLogin(string User) : AuditCommand;

public sealed record Ping : IRequest;

public sealed record Unhandled : IRequest;
