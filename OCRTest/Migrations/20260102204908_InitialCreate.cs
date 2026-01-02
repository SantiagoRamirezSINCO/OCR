using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OCRTest.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    photo_path = table.Column<string>(type: "text", nullable: false),
                    raw_ocr_text = table.Column<string>(type: "text", nullable: true),
                    extracted_fields = table.Column<string>(type: "jsonb", nullable: true),
                    confidence_scores = table.Column<string>(type: "jsonb", nullable: true),
                    processing_status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    processing_time_ms = table.Column<long>(type: "bigint", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_receipts_created_at",
                table: "receipts",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "idx_receipts_filename",
                table: "receipts",
                column: "file_name");

            migrationBuilder.CreateIndex(
                name: "idx_receipts_status",
                table: "receipts",
                column: "processing_status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "receipts");
        }
    }
}
