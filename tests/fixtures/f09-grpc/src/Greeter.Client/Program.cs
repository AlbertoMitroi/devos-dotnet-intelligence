using Greet;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpcClient<Greeter.GreeterClient>(o => o.Address = new Uri("http://greeter"));
var app = builder.Build();
var client = app.Services.GetRequiredService<Greeter.GreeterClient>();
var reply = await client.SayHelloAsync(new HelloRequest { Name = "x" });
Console.WriteLine(reply.Message);
