using Lib;
using Microsoft.Extensions.DependencyInjection;

namespace App;

public interface IRepository { }
public sealed class Repository : IRepository { }
public interface ICache { }
public sealed class MemoryCache : ICache { }
public sealed class Reporter(IClock clock, IRepository repository)
{
    public IClock Clock { get; } = clock;
    public IRepository Repository { get; } = repository;
}
public sealed class KeyedUser([FromKeyedServices("fast")] ICache cache)
{
    public ICache Cache { get; } = cache;
}
public sealed class Worker : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
public sealed class WeatherClient(HttpClient http)
{
    public Task<string> GetAsync() => http.GetStringAsync("forecast");
}
