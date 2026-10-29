using Marten;
using MediatR;
using Microsoft.AspNetCore.SignalR;

namespace Leasing;

public class ApproveLeaseHandler(IDocumentSession session, IPublisher publisher) : IRequestHandler<ApproveLease, bool>
{
    public async Task<bool> Handle(ApproveLease request, CancellationToken cancellationToken)
    {
        session.Events.StartStream<Lease>(request.LeaseId, new LeaseCreated(request.LeaseId, "acme"));
        session.Events.Append(request.LeaseId, new LeaseCancelled(request.LeaseId));
        session.Store(new Customer());
        await session.SaveChangesAsync(cancellationToken);
        await publisher.Publish(new LeaseApproved(request.LeaseId), cancellationToken);
        return true;
    }
}

public class NotifyUsers(IHubContext<NotificationsHub> hub) : INotificationHandler<LeaseApproved>
{
    public Task Handle(LeaseApproved notification, CancellationToken cancellationToken) =>
        hub.Clients.All.SendAsync("leaseApproved", notification.LeaseId, cancellationToken);
}

public class IndexLease(Elastic.Clients.Elasticsearch.ElasticsearchClient client) : INotificationHandler<LeaseApproved>
{
    public async Task Handle(LeaseApproved notification, CancellationToken cancellationToken)
    {
        await client.IndexAsync(new LeaseDocument { Id = notification.LeaseId }, i => i.Index("leases"), cancellationToken);
    }
}

public class NotificationsHub : Hub
{
    public Task Join(string team) => Clients.Group(team).SendAsync("joined", team);
}

public class LeaseSummaryProjection : Marten.Events.Aggregation.SingleStreamProjection<LeaseSummary, Guid>
{
    public LeaseSummary Create(LeaseCreated created) => new() { Id = created.LeaseId };

    public void Apply(LeaseCancelled cancelled, LeaseSummary summary) { }
}
