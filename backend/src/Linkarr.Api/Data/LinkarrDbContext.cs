using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Linkarr.Api.Data;

// The single <config dir>/linkarr.db.
public sealed class LinkarrDbContext(DbContextOptions<LinkarrDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AccountAvatar> Avatars => Set<AccountAvatar>();
    public DbSet<CatalogItem> Catalog => Set<CatalogItem>();
    public DbSet<TitleRequest> Requests => Set<TitleRequest>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<CatalogSnapshot> Snapshots => Set<CatalogSnapshot>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Account>(account =>
        {
            account.Ignore(a => a.Rights);
            account.HasIndex(a => a.Username).IsUnique();
            account.Property(a => a.Username).UseCollation("NOCASE");
            account.HasIndex(a => a.ExternalId).IsUnique();
            // An app user belongs to one account at most (SQLite allows any number of NULLs).
            account.HasIndex(a => a.VisularrId).IsUnique();
            account.HasIndex(a => a.ModularrId).IsUnique();
        });
        modelBuilder.Entity<Session>(session =>
        {
            session.HasIndex(s => s.AccountId);
            session.HasOne<Account>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AccountAvatar>(avatar =>
        {
            avatar.HasKey(a => a.AccountId);
            avatar.HasOne<Account>().WithOne().HasForeignKey<AccountAvatar>(a => a.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<CatalogItem>().HasKey(c => c.Key);
        modelBuilder.Entity<CatalogSnapshot>().HasKey(s => s.Date);
        modelBuilder.Entity<TitleRequest>(request =>
        {
            request.HasIndex(r => r.AccountId);
            request.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

// SQLite keeps no DateTimeKind: everything is written as UTC and read back as UTC.
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

// Lets `dotnet ef migrations add` build the model without starting the app.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LinkarrDbContext>
{
    public LinkarrDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LinkarrDbContext>().UseSqlite("Data Source=linkarr.db").Options);
}
