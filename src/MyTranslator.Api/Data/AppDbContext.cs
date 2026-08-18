using Microsoft.EntityFrameworkCore;

namespace MyTranslator.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<TranslationTask> TranslationTasks => Set<TranslationTask>();
    public DbSet<TranslationSegment> TranslationSegments => Set<TranslationSegment>();
    public DbSet<ProtectedBlock> ProtectedBlocks => Set<ProtectedBlock>();
    public DbSet<TranslationRun> TranslationRuns => Set<TranslationRun>();
    public DbSet<TranslationRunFailure> TranslationRunFailures => Set<TranslationRunFailure>();
    public DbSet<AiProvider> Providers => Set<AiProvider>();
    public DbSet<AiModel> Models => Set<AiModel>();
    public DbSet<Term> Terms => Set<Term>();

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
        task.Property(entity => entity.Status)
            .HasConversion(
                status => status.ToWireValue(),
                value => TranslationTaskStatusExtensions.ParseWireValue(value))
            .HasMaxLength(20)
            .IsRequired();
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
        segment.Property(entity => entity.ConfirmationStatus)
            .HasConversion(
                status => status.ToWireValue(),
                value => SegmentConfirmationStatusExtensions.ParseWireValue(value))
            .HasMaxLength(20)
            .IsRequired();
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

        var run = modelBuilder.Entity<TranslationRun>();
        run.ToTable("TranslationRuns");
        run.HasKey(entity => entity.Id);
        run.Property(entity => entity.Status)
            .HasConversion(
                status => status.ToWireValue(),
                value => TranslationRunStatusExtensions.ParseWireValue(value))
            .HasMaxLength(20)
            .IsRequired();
        run.Property(entity => entity.SourceLanguage).HasMaxLength(50);
        run.Property(entity => entity.TargetLanguage).HasMaxLength(50).IsRequired();
        run.Property(entity => entity.FailureCode).HasMaxLength(80);
        run.HasIndex(entity => entity.ActiveTaskLockId)
            .IsUnique()
            .HasDatabaseName("IX_TranslationRuns_ActiveTaskId")
            .HasFilter("\"ActiveTaskId\" IS NOT NULL");
        run.HasIndex(entity => new { entity.TaskId, entity.CreatedAt });
        run.HasOne(entity => entity.Task)
            .WithMany(entity => entity.TranslationRuns)
            .HasForeignKey(entity => entity.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        var failure = modelBuilder.Entity<TranslationRunFailure>();
        failure.ToTable("TranslationRunFailures");
        failure.HasKey(entity => entity.Id);
        failure.Property(entity => entity.Code).HasMaxLength(80).IsRequired();
        failure.HasIndex(entity => new { entity.RunId, entity.SegmentOrder }).IsUnique();
        failure.HasOne(entity => entity.Run)
            .WithMany(entity => entity.Failures)
            .HasForeignKey(entity => entity.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        var provider = modelBuilder.Entity<AiProvider>();
        provider.ToTable("Providers");
        provider.HasKey(entity => entity.Id);
        provider.Property(entity => entity.Name).HasMaxLength(200).IsRequired();
        provider.Property(entity => entity.Kind).HasMaxLength(30).IsRequired();
        provider.Property(entity => entity.BaseUrl).HasMaxLength(2000);
        provider.Property(entity => entity.ApiKey);
        provider.HasIndex(entity => entity.IsDefault)
            .IsUnique()
            .HasFilter("\"IsDefault\" = 1");
        provider.HasIndex(entity => entity.CreatedAt);

        var model = modelBuilder.Entity<AiModel>();
        model.ToTable("Models");
        model.HasKey(entity => entity.Id);
        model.Property(entity => entity.ModelId).HasMaxLength(500).IsRequired();
        model.Property(entity => entity.DisplayName).HasMaxLength(200).IsRequired();
        model.HasIndex(entity => new { entity.ProviderId, entity.ModelId }).IsUnique();
        model.HasIndex(entity => entity.ProviderId)
            .IsUnique()
            .HasFilter("\"IsDefault\" = 1");
        model.HasIndex(entity => entity.CreatedAt);
        model.HasOne(entity => entity.Provider)
            .WithMany(entity => entity.Models)
            .HasForeignKey(entity => entity.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);

        var term = modelBuilder.Entity<Term>();
        term.ToTable("Terms");
        term.HasKey(entity => entity.Id);
        term.Property(entity => entity.SourceTerm).HasMaxLength(1000).IsRequired();
        term.Property(entity => entity.SourceTermKey).HasMaxLength(1000).IsRequired();
        term.Property(entity => entity.TargetTerm).HasMaxLength(1000).IsRequired();
        term.Property(entity => entity.SourceLanguage).HasMaxLength(50).IsRequired();
        term.Property(entity => entity.TargetLanguage).HasMaxLength(50).IsRequired();
        term.Property(entity => entity.Notes).HasMaxLength(4000);
        term.Property(entity => entity.Version).IsConcurrencyToken();
        term.HasIndex(entity => new
        {
            entity.SourceLanguage,
            entity.TargetLanguage,
            entity.SourceTermKey
        }).IsUnique();
        term.HasIndex(entity => new { entity.UpdatedAt, entity.Id });
    }
}
