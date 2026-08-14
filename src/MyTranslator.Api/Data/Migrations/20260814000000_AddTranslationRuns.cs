using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260814000000_AddTranslationRuns")]
public partial class AddTranslationRuns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TranslationRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                ActiveTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                ExtractionRevision = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                SourceLanguage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                TargetLanguage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                TotalSegments = table.Column<int>(type: "INTEGER", nullable: false),
                SelectedSegments = table.Column<int>(type: "INTEGER", nullable: false),
                SkippedExistingSegments = table.Column<int>(type: "INTEGER", nullable: false),
                ProcessedSegments = table.Column<int>(type: "INTEGER", nullable: false),
                SucceededSegments = table.Column<int>(type: "INTEGER", nullable: false),
                FailedSegments = table.Column<int>(type: "INTEGER", nullable: false),
                FailureCode = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                FailureRetryable = table.Column<bool>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TranslationRuns", x => x.Id);
                table.ForeignKey(
                    name: "FK_TranslationRuns_TranslationTasks_TaskId",
                    column: x => x.TaskId,
                    principalTable: "TranslationTasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TranslationRunFailures",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                SegmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                SegmentOrder = table.Column<int>(type: "INTEGER", nullable: false),
                Code = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                Retryable = table.Column<bool>(type: "INTEGER", nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TranslationRunFailures", x => x.Id);
                table.ForeignKey(
                    name: "FK_TranslationRunFailures_TranslationRuns_RunId",
                    column: x => x.RunId,
                    principalTable: "TranslationRuns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TranslationRunFailures_RunId_SegmentOrder",
            table: "TranslationRunFailures",
            columns: new[] { "RunId", "SegmentOrder" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TranslationRuns_ActiveTaskId",
            table: "TranslationRuns",
            column: "ActiveTaskId",
            unique: true,
            filter: "\"ActiveTaskId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_TranslationRuns_TaskId_CreatedAt",
            table: "TranslationRuns",
            columns: new[] { "TaskId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TranslationRunFailures");
        migrationBuilder.DropTable(name: "TranslationRuns");
    }
}
