using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog;

public class Product { public int Id { get; set; } }

[Table("purchase_orders", Schema = "sales")]
public class Order { public int Id { get; set; } }

public class Customer { public int Id { get; set; } }

public class Invoice { public int Id { get; set; } }

public static class CatalogDbProperties
{
    public static string DbTablePrefix { get; set; } = "Catalog";
}

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");
        modelBuilder.Entity<Product>(b => b.ToTable("products", "inventory"));
        modelBuilder.ApplyConfiguration(new InvoiceConfiguration());
    }
}

public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder) => builder.ToTable(CatalogDbProperties.DbTablePrefix + "Invoices");
}
