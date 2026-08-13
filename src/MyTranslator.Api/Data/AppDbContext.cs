using Microsoft.EntityFrameworkCore;

namespace MyTranslator.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<TranslationTask> TranslationTasks => Set<TranslationTask>();
    public DbSet<TranslationSegment> TranslationSegments => Set<TranslationSegment>();
    public DbSet<ProtectedBlock> ProtectedBlocks => Set<ProtectedBlock>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var token = modelBuilder.Entity<ApiToken>();
        token.ToTable("ApiTokens");
        token.HasKey(entity => entity.Id);
        token.Property(entity => entity.Name).HasMaxLength(100).IsRequired();
        token.Property(entity => entity.TokenHash).HasMaxLength(64).IsRequired();
        token.Property(entity => entity.TokenPrefix).HasMaxLength(15).IsRequired();
        token.HasIndex(entity => entity.TokenHash).IsUnique();

        var task = modelBuilder.Entity<TranslationTask>();
        task.ToTable("TranslationTasks");
        task.HasKey(entity => entity.Id);
        task.Property(entity => entity.Status).HasMaxLength(20).IsRequired();
        task.Property(entity => entity.SourceKind).HasMaxLength(20).IsRequired();
        task.Property(entity => entity.FileName).HasMaxLength(255).IsRequired();
        task.Property(entity => entity.MediaType).HasMaxLength(100).IsRequired();
        task.Property(entity => entity.FileType).HasMaxLength(20).IsRequired();
        task.Property(entity => entity.OriginalEncoding).HasMaxLength(50);
        task.Property(entity => entity.SourceBytes).IsRequired();
        task.Property(entity => entity.ReconstructionTemplate).IsRequired();

        var segment = modelBuilder.Entity<TranslationSegment>();
        segment.ToTable("TranslationSegments");
        segment.HasKey(entity => entity.Id);
        segment.Property(entity => entity.SourceText).IsRequired();
        segment.Property(entity => entity.ConfirmationStatus).HasMaxLength(20).IsRequired();
        segment.Property(entity => entity.MarkupTableJson).IsRequired();
        segment.Property(entity => entity.TemplateToken).HasMaxLength(80).IsRequired();
        segment.HasIndex(entity => new { entity.TaskId, entity.Order }).IsUnique();
        segment.HasOne(entity => entity.Task)
            .WithMany(entity => entity.Segments)
            .HasForeignKey(entity => entity.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        var protectedBlock = modelBuilder.Entity<ProtectedBlock>();
        protectedBlock.ToTable("ProtectedBlocks");
        protectedBlock.HasKey(entity => entity.Id);
        protectedBlock.Property(entity => entity.Type).HasMaxLength(50).IsRequired();
        protectedBlock.Property(entity => entity.PreviewText).IsRequired();
        protectedBlock.Property(entity => entity.ContentHash).HasMaxLength(71).IsRequired();
        protectedBlock.HasIndex(entity => new { entity.TaskId, entity.SourceUnitOrder }).IsUnique();
        protectedBlock.HasOne(entity => entity.Task)
            .WithMany(entity => entity.ProtectedBlocks)
            .HasForeignKey(entity => entity.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
