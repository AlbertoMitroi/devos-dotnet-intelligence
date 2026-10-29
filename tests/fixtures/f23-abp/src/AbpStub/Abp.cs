using System.Reflection;

namespace Volo.Abp.Modularity
{
    public abstract class AbpModule
    {
        public virtual void ConfigureServices(ServiceConfigurationContext context) { }
    }

    public sealed class ServiceConfigurationContext;

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class DependsOnAttribute(params Type[] dependedTypes) : Attribute
    {
        public Type[] DependedTypes { get; } = dependedTypes;
    }
}

namespace Volo.Abp.EventBus
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class EventNameAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }
}

namespace Volo.Abp.EventBus.Distributed
{
    public interface IDistributedEventBus
    {
        Task PublishAsync<TEvent>(TEvent eventData, bool onUnitOfWorkComplete = true, bool useOutbox = true) where TEvent : class;
    }

    public interface IDistributedEventHandler<in TEvent>
    {
        Task HandleEventAsync(TEvent eventData);
    }
}

namespace Volo.Abp.EventBus.RabbitMq
{
    public sealed class AbpEventBusRabbitMqModule : Volo.Abp.Modularity.AbpModule;
}

namespace Volo.Abp.Application.Services
{
    public interface IApplicationService;

    public abstract class ApplicationService : IApplicationService;
}

namespace Volo.Abp.AspNetCore.Mvc.Conventions
{
    public sealed class ConventionalControllerSetting
    {
        public string RootPath { get; set; } = "app";
        public string RemoteServiceName { get; set; } = "Default";
    }

    public sealed class ConventionalControllerSettingList : List<ConventionalControllerSetting>
    {
        public ConventionalControllerSettingList Create(Assembly assembly, Action<ConventionalControllerSetting>? optionsAction = null) => this;
    }
}

namespace Volo.Abp.AspNetCore.Mvc
{
    public sealed class AbpAspNetCoreMvcOptions
    {
        public Conventions.ConventionalControllerSettingList ConventionalControllers { get; } = [];
    }
}

namespace Volo.Abp.Http.Client.ClientProxying
{
    public sealed class ClientProxyRequestTypeValue : List<KeyValuePair<Type, object?>>
    {
        public void Add(Type type, object? value) => Add(new KeyValuePair<Type, object?>(type, value));
    }

    public abstract class ClientProxyBase<TService>
    {
        protected Task<T> RequestAsync<T>(string methodName, ClientProxyRequestTypeValue? arguments = null) => Task.FromResult(default(T)!);

        protected Task RequestAsync(string methodName, ClientProxyRequestTypeValue? arguments = null) => Task.CompletedTask;
    }
}
