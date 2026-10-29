using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var catalog = builder.AddDotnetProject("catalog", "../Catalog");
builder.AddDotnetProject("store", "../Store").WithReference(catalog);
