using OCRTest.Models;

namespace OCRTest.Services;

public interface IReceiptProcessingService
{
    // Existing methods (legacy - file-based)
    Task<ReceiptResponse> ProcessReceiptAsync(string fileName, CancellationToken cancellationToken = default);
    Task<BatchProcessingResponse> ProcessAllReceiptsAsync(CancellationToken cancellationToken = default);
    Task<List<FileInfo>> GetAvailableReceiptsAsync();

    // New methods (database-backed)
    Task<ReceiptUploadResponse> ProcessUploadedReceiptAsync(IFormFile file, CancellationToken cancellationToken = default);
    Task<ReceiptEntity?> GetReceiptByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PagedReceiptResponse> GetReceiptsPagedAsync(int page, int pageSize, ProcessingStatus? status = null, CancellationToken cancellationToken = default);
}
