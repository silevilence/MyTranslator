using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTranslator.Api.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260819120000_AddReviewRuns")]
public partial class AddReviewRuns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ReviewRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                ActiveTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                ExtractionRevision = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                SourceLanguage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                TargetLanguage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                ModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                TermSnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                TotalSegments = table.Column<int>(type: "INTEGER", nullable: false),
                SelectedSegments = table.Column<int>(type: "INTEGER", nullable: false),
                SkippedUntranslatedSegments = table.Column<int>(type: "INTEGER", nullable: false),
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
                table.PrimaryKey("PK_ReviewRuns", x => x.Id);
                table.ForeignKey(
                    name: "FK_ReviewRuns_TranslationTasks_TaskId",
                    column: x => x.TaskId,
                    principalTable: "TranslationTasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ReviewRunFailures",
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
                table.PrimaryKey("PK_ReviewRunFailures", x => x.Id);
                table.ForeignKey(
                    name: "FK_ReviewRunFailures_ReviewRuns_RunId",
                    column: x => x.RunId,
                    principalTable: "ReviewRuns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ReviewRunSegments",
            columns: table => new
            {
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                SegmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                SegmentOrder = table.Column<int>(type: "INTEGER", nullable: false),
                SourceText = table.Column<string>(type: "TEXT", nullable: false),
                TargetText = table.Column<string>(type: "TEXT", nullable: false),
                MarkupTableJson = table.Column<string>(type: "TEXT", nullable: false),
                Processed = table.Column<bool>(type: "INTEGER", nullable: false),
                Succeeded = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReviewRunSegments", x => new { x.RunId, x.SegmentId });
                table.ForeignKey(
                    name: "FK_ReviewRunSegments_ReviewRuns_RunId",
                    column: x => x.RunId,
                    principalTable: "ReviewRuns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ReviewRunSegments_TranslationSegments_SegmentId",
                    column: x => x.SegmentId,
                    principalTable: "TranslationSegments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ReviewComments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                SegmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                Position = table.Column<int>(type: "INTEGER", nullable: false),
                Severity = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                Issue = table.Column<string>(type: "TEXT", nullable: false),
                Suggestion = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReviewComments", x => x.Id);
                table.ForeignKey(
                    name: "FK_ReviewComments_ReviewRuns_RunId",
                    column: x => x.RunId,
                    principalTable: "ReviewRuns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ReviewComments_TranslationSegments_SegmentId",
                    column: x => x.SegmentId,
                    principalTable: "TranslationSegments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ReviewComments_RunId",
            table: "ReviewComments",
            column: "RunId");
        migrationBuilder.CreateIndex(
            name: "IX_ReviewComments_SegmentId_Position",
            table: "ReviewComments",
            columns: new[] { "SegmentId", "Position" });
        migrationBuilder.CreateIndex(
            name: "IX_ReviewRunFailures_RunId_SegmentOrder",
            table: "ReviewRunFailures",
            columns: new[] { "RunId", "SegmentOrder" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ReviewRuns_ActiveTaskId",
            table: "ReviewRuns",
            column: "ActiveTaskId",
            unique: true,
            filter: "\"ActiveTaskId\" IS NOT NULL");
        migrationBuilder.CreateIndex(
            name: "IX_ReviewRuns_TaskId_CreatedAt",
            table: "ReviewRuns",
            columns: new[] { "TaskId", "CreatedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_ReviewRunSegments_RunId_SegmentOrder",
            table: "ReviewRunSegments",
            columns: new[] { "RunId", "SegmentOrder" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ReviewRunSegments_SegmentId",
            table: "ReviewRunSegments",
            column: "SegmentId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ReviewComments");
        migrationBuilder.DropTable(name: "ReviewRunFailures");
        migrationBuilder.DropTable(name: "ReviewRunSegments");
        migrationBuilder.DropTable(name: "ReviewRuns");
    }
}
