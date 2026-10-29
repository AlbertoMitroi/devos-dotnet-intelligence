namespace Leasing;

public record ApproveLease(Guid LeaseId) : MediatR.IRequest<bool>;

public record LeaseApproved(Guid LeaseId) : MediatR.INotification;

public record LeaseCreated(Guid LeaseId, string Customer);

public record LeaseCancelled(Guid LeaseId);

public class Lease
{
    public Guid Id { get; set; }
    public void Apply(LeaseCreated created) => Id = created.LeaseId;
}

public class LeaseSummary
{
    public Guid Id { get; set; }
}

public class Customer
{
    public Guid Id { get; set; }
}

public class LeaseDocument
{
    public Guid Id { get; set; }
}
