var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/api/items/{id:int}", (int id) => id);
app.MapGet("/api/items/by", (string ids) => ids);
app.Run();
