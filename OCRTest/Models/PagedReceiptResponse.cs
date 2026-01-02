namespace OCRTest.Models;

public class PagedReceiptResponse
{
    public List<ReceiptSummary> Receipts { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

public class ReceiptSummary
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ProcessingStatus { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
