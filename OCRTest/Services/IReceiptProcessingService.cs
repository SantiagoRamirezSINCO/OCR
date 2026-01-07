using OCRTest.Models;

namespace OCRTest.Services;

public interface IReceiptProcessingService
{
    Task<ReceiptResponse> ProcessReceiptAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}
