using Flow.Engine;

namespace Leasing;

public record ApproveLeaseCommand(bool Approved);

public record LeaseSigned(Guid LeaseId);

public class CreditCheckPlugin : IHandleServiceTask
{
    public string TaskName => "Run credit check";
    public Task ExecuteAsync(Guid flowId) => Task.CompletedTask;
}

public class ArchiveLeasePlugin : IHandleServiceTask
{
    public string TaskName => "Archive lease";
    public Task ExecuteAsync(Guid flowId) => Task.CompletedTask;
}

[Task("Approve lease")]
public class ApproveLeasePlugin : IHandleUserTaskCommand<ApproveLeaseCommand>
{
    public Task HandleAsync(Guid flowId, ApproveLeaseCommand command) => Task.CompletedTask;
}

public class LeaseSignedHandler : IHandleFlowEvent<LeaseSigned>
{
    public Task HandleAsync(Guid flowId, LeaseSigned flowEvent) => Task.CompletedTask;
}

public class LeaseRequestedPlugin : IHandleNewFlow
{
    public Task StartAsync(Guid flowId) => Task.CompletedTask;
}
