using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261028000000_AddTaskReferences")]
public sealed class AddTaskReferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Glossaries", columns: table => new
        {
            Id = table.Column<Guid>(type: "TEXT", nullable: false),
            Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
            Version = table.Column<int>(type: "INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Glossaries", x => x.Id));
        migrationBuilder.Sql("INSERT INTO Glossaries (Id, Name, Version) VALUES ('00000000-0000-0000-0000-000000000001', '默认术语表', 1);");
        migrationBuilder.AddColumn<Guid>("GlossaryId", "Terms", type: "TEXT", nullable: false, defaultValue: Glossary.DefaultId);
        migrationBuilder.AddColumn<Guid>("GlossaryId", "TranslationTasks", type: "TEXT", nullable: true, defaultValue: Glossary.DefaultId);
        migrationBuilder.AddColumn<int>("ReferenceVersion", "TranslationTasks", type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<string>("BackgroundInformation", "TranslationTasks", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("BackgroundInformation", "TranslationRuns", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("BackgroundInformation", "ReviewRuns", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("TermSnapshotJson", "TranslationRuns", type: "TEXT", nullable: false, defaultValue: "[]");
        migrationBuilder.AddColumn<string>("ReasoningEffort", "ReviewRuns", type: "TEXT", nullable: true);
        migrationBuilder.DropIndex("IX_Terms_SourceLanguage_TargetLanguage_SourceTermKey", "Terms");
        migrationBuilder.CreateIndex("IX_Terms_GlossaryId_SourceLanguage_TargetLanguage_SourceTermKey", "Terms",
            new[] { "GlossaryId", "SourceLanguage", "TargetLanguage", "SourceTermKey" }, unique: true);
        migrationBuilder.CreateIndex("IX_TranslationTasks_GlossaryId", "TranslationTasks", "GlossaryId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // 存在跨表同名词条时旧唯一索引会拒绝回退，事务回滚，不删除任何词条。
        migrationBuilder.CreateIndex("IX_Terms_SourceLanguage_TargetLanguage_SourceTermKey", "Terms",
            new[] { "SourceLanguage", "TargetLanguage", "SourceTermKey" }, unique: true);
        migrationBuilder.DropIndex("IX_Terms_GlossaryId_SourceLanguage_TargetLanguage_SourceTermKey", "Terms");
        migrationBuilder.DropIndex("IX_TranslationTasks_GlossaryId", "TranslationTasks");
        migrationBuilder.Sql("ALTER TABLE Terms DROP COLUMN GlossaryId;");
        migrationBuilder.Sql("ALTER TABLE TranslationTasks DROP COLUMN GlossaryId;");
        migrationBuilder.Sql("ALTER TABLE TranslationTasks DROP COLUMN ReferenceVersion;");
        migrationBuilder.Sql("ALTER TABLE TranslationTasks DROP COLUMN BackgroundInformation;");
        migrationBuilder.Sql("ALTER TABLE TranslationRuns DROP COLUMN BackgroundInformation;");
        migrationBuilder.Sql("ALTER TABLE TranslationRuns DROP COLUMN TermSnapshotJson;");
        migrationBuilder.Sql("ALTER TABLE ReviewRuns DROP COLUMN BackgroundInformation;");
        migrationBuilder.Sql("ALTER TABLE ReviewRuns DROP COLUMN ReasoningEffort;");
        migrationBuilder.DropTable("Glossaries");
    }
}
