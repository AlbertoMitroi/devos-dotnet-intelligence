using Elastic.Clients.Elasticsearch;
using JasperFx.Events.Projections;
using Leasing;
using Marten;
using MediatR;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMediatR(c => c.RegisterServicesFromAssemblyContaining<ApproveLease>());
builder.Services.AddSignalR();
builder.Services.AddMarten(opts =>
{
    opts.Connection(builder.Configuration.GetConnectionString("Leasing")!);
    opts.Projections.Add<LeaseSummaryProjection>(ProjectionLifecycle.Async);
    opts.Projections.Snapshot<Lease>(SnapshotLifecycle.Inline);
    opts.Schema.For<LeaseDocument>().DocumentAlias("lease_documents");
});
builder.Services.AddSingleton(new ElasticsearchClient(new ElasticsearchClientSettings(new Uri("http://search:9200"))
    .DefaultMappingFor<Customer>(m => m.IndexName(builder.Configuration["Search:CustomerIndex"]!))));

var app = builder.Build();
app.MapHub<NotificationsHub>("/hubs/notifications");
app.MapPost("/v1/leases/{id}/approve", (Guid id, ISender sender) => sender.Send(new ApproveLease(id)));
app.MapGet("/v1/customers", async (ElasticsearchClient client) => await client.SearchAsync<Customer>(s => s.Size(10)));
app.Run();
