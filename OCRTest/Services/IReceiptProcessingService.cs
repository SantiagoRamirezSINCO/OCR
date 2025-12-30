using OCRTest.Models;

namespace OCRTest.Services;

public interface IReceiptProcessingService
{
    Task<ReceiptResponse> ProcessReceiptAsync(string fileName, CancellationToken cancellationToken = default);
    Task<BatchProcessingResponse> ProcessAllReceiptsAsync(CancellationToken cancellationToken = default);
    Task<List<FileInfo>> GetAvailableReceiptsAsync();
}
