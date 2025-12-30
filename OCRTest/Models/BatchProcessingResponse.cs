namespace OCRTest.Models;

public class BatchProcessingResponse
{
    public int TotalProcessed { get; set; }
    public int Successful { get; set; }
    public int Failed { get; set; }
    public List<ReceiptResponse> Results { get; set; } = new();
}
