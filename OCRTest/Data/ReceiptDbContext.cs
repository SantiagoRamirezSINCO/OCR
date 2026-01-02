using Microsoft.EntityFrameworkCore;
using OCRTest.Models;

namespace OCRTest.Data;

public class ReceiptDbContext : DbContext
{
    public DbSet<ReceiptEntity> Receipts { get; set; } = null!;

    public ReceiptDbContext(DbContextOptions<ReceiptDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ReceiptEntity>(entity =>
        {
            entity.ToTable("receipts");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id");

            entity.Property(e => e.FileName)
                .IsRequired()
                .HasMaxLength(255)
                .HasColumnName("file_name");

            entity.Property(e => e.FileSizeBytes)
                .IsRequired()
                .HasColumnName("file_size_bytes");

            entity.Property(e => e.MimeType)
                .IsRequired()
                .HasMaxLength(100)
                .HasColumnName("mime_type");

            entity.Property(e => e.PhotoPath)
                .IsRequired()
                .HasColumnName("photo_path");

            entity.Property(e => e.RawOcrText)
                .HasColumnType("text")
                .HasColumnName("raw_ocr_text");

            entity.Property(e => e.ExtractedFieldsJson)
                .HasColumnType("jsonb")
                .HasColumnName("extracted_fields");

            entity.Property(e => e.ConfidenceScoresJson)
                .HasColumnType("jsonb")
                .HasColumnName("confidence_scores");

            entity.Property(e => e.ProcessingStatus)
                .IsRequired()
                .HasConversion(
                    v => v.ToString().ToLowerInvariant(),
                    v => Enum.Parse<ProcessingStatus>(v, ignoreCase: true))
                .HasMaxLength(50)
                .HasColumnName("processing_status");

            entity.Property(e => e.ProcessingTimeMs)
                .HasColumnName("processing_time_ms");

            entity.Property(e => e.ErrorMessage)
                .HasColumnType("text")
                .HasColumnName("error_message");

            entity.Property(e => e.CreatedAt)
                .IsRequired()
                .HasDefaultValueSql("NOW()")
                .HasColumnName("created_at");

            entity.Property(e => e.UpdatedAt)
                .IsRequired()
                .HasDefaultValueSql("NOW()")
                .HasColumnName("updated_at");

            entity.Property(e => e.ProcessedAt)
                .HasColumnName("processed_at");

            // Indexes
            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("idx_receipts_created_at")
                .IsDescending();

            entity.HasIndex(e => e.ProcessingStatus)
                .HasDatabaseName("idx_receipts_status");

            entity.HasIndex(e => e.FileName)
                .HasDatabaseName("idx_receipts_filename");
        });
    }
}
