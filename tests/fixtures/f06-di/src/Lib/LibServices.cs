using Microsoft.Extensions.DependencyInjection;

namespace Lib;

public interface IClock { }

public sealed class SystemClock : IClock { }

public static class LibServices
{
    public static IServiceCollection AddLib(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        return services;
    }
}
