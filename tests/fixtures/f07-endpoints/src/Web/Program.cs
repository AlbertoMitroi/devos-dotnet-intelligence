using Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
var app = builder.Build();
const string ItemsRoute = "/items";
var api = app.MapGroup("/api");
var v1 = api.MapGroup("v1");
v1.MapGet(ItemsRoute, () => "all");
v1.MapPost(ItemsRoute + "/{id:int}", (int id) => id);
app.MapMethods("/health", ["GET", "HEAD"], () => "ok");
app.MapControllers();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
