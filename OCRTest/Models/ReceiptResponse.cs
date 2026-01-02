namespace OCRTest.Models;

public class ReceiptResponse
{
    public bool Success { get; set; }
    public string FileName { get; set; } = string.Empty;
    public Guid? ReceiptId { get; set; }
    public ReceiptData? Data { get; set; }
    public long? ProcessingTimeMs { get; set; }
    public ConfidenceScores? Confidence { get; set; }
    public ProcessingError? Error { get; set; }
}
