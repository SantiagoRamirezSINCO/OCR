namespace OCRTest.Models;

public class ReceiptEntity
{
    public Guid Id { get; set; }

    // File metadata
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string PhotoPath { get; set; } = string.Empty;

    // OCR results
    public string? RawOcrText { get; set; }
    public string? ExtractedFieldsJson { get; set; }
    public string? ConfidenceScoresJson { get; set; }

    // Processing metadata
    public ProcessingStatus ProcessingStatus { get; set; }
    public long? ProcessingTimeMs { get; set; }
    public string? ErrorMessage { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

public enum ProcessingStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}
