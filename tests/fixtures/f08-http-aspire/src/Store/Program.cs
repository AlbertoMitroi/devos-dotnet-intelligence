using Store;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri("http://catalog"));
builder.Build().Run();
