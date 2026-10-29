using App;
using Lib;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLib();
builder.Services.AddScoped<IRepository, Repository>();
builder.Services.AddTransient(typeof(Reporter));
builder.Services.AddSingleton<ICache>(sp => new MemoryCache());
builder.Services.AddKeyedSingleton<ICache, MemoryCache>("fast");
builder.Services.TryAddScoped<KeyedUser>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHttpClient<WeatherClient>(c => c.BaseAddress = new Uri("http://weather"));
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<Repository>();
}
var app = builder.Build();
var reporter = app.Services.GetRequiredService<Reporter>();
app.Run();
