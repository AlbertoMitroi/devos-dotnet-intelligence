namespace Bus;

public abstract record EventBase;

public interface IMessageBus
{
    Task PublishAsync(EventBase message);
}

public interface IHandles<in T> where T : EventBase
{
    Task HandleAsync(T message);
}

public static class Subscriptions
{
    public static void Subscribe<T, THandler>() where T : EventBase where THandler : IHandles<T> { }
}
