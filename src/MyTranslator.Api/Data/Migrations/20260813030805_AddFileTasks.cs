using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTranslator.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFileTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TranslationTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    RequestedUrl = table.Column<string>(type: "TEXT", nullable: true),
                    FinalUrl = table.Column<string>(type: "TEXT", nullable: true),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    FileType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OriginalEncoding = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SourceBytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ReconstructionTemplate = table.Column<string>(type: "TEXT", nullable: false),
                    ExtractionRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    SegmentationRequested = table.Column<string>(type: "TEXT", nullable: true),
                    SegmentationRecommended = table.Column<string>(type: "TEXT", nullable: true),
                    SegmentationEffective = table.Column<string>(type: "TEXT", nullable: true),
                    SegmentationReason = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedBlockCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ChapterCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranslationTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProtectedBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceUnitOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PreviewText = table.Column<string>(type: "TEXT", nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ChapterJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectedBlocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProtectedBlocks_TranslationTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TranslationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TranslationSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceUnitOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceText = table.Column<string>(type: "TEXT", nullable: false),
                    TargetText = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmationStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    MarkupTableJson = table.Column<string>(type: "TEXT", nullable: false),
                    ChapterJson = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateToken = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranslationSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TranslationSegments_TranslationTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TranslationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProtectedBlocks_TaskId_SourceUnitOrder",
                table: "ProtectedBlocks",
                columns: new[] { "TaskId", "SourceUnitOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TranslationSegments_TaskId_Order",
                table: "TranslationSegments",
                columns: new[] { "TaskId", "Order" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProtectedBlocks");

            migrationBuilder.DropTable(
                name: "TranslationSegments");

            migrationBuilder.DropTable(
                name: "TranslationTasks");
        }
    }
}
