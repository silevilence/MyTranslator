using Microsoft.EntityFrameworkCore;

namespace MyTranslator.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var token = modelBuilder.Entity<ApiToken>();
        token.ToTable("ApiTokens");
        token.HasKey(entity => entity.Id);
        token.Property(entity => entity.Name).HasMaxLength(100).IsRequired();
        token.Property(entity => entity.TokenHash).HasMaxLength(64).IsRequired();
        token.Property(entity => entity.TokenPrefix).HasMaxLength(15).IsRequired();
        token.HasIndex(entity => entity.TokenHash).IsUnique();
    }
}
