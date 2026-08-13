using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
partial class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9");

        modelBuilder.Entity("MyTranslator.Api.Data.ApiToken", entity =>
        {
            entity.Property<Guid>("Id").HasColumnType("TEXT");
            entity.Property<DateTimeOffset>("CreatedAt").HasColumnType("TEXT");
            entity.Property<string>("Name").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            entity.Property<DateTimeOffset?>("RevokedAt").HasColumnType("TEXT");
            entity.Property<string>("TokenHash").IsRequired().HasMaxLength(64).HasColumnType("TEXT");
            entity.Property<string>("TokenPrefix").IsRequired().HasMaxLength(15).HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.HasIndex("TokenHash").IsUnique();
            entity.ToTable("ApiTokens");
        });
    }
}
