namespace Flow.Engine;

/// <summary>A stand-in for an in-house workflow engine's plugin contracts (named only through the conventions file).</summary>
public interface IHandleServiceTask
{
    string TaskName { get; }
    Task ExecuteAsync(Guid flowId);
}

public interface IHandleUserTaskCommand<TCommand>
{
    Task HandleAsync(Guid flowId, TCommand command);
}

public interface IHandleFlowEvent<TEvent>
{
    Task HandleAsync(Guid flowId, TEvent flowEvent);
}

public interface IHandleNewFlow
{
    Task StartAsync(Guid flowId);
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class TaskAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
