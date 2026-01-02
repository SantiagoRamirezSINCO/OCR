namespace OCRTest.Services;

public interface IReceiptStorageService
{
    Task<string> SavePhotoAsync(IFormFile file, Guid receiptId, CancellationToken cancellationToken = default);
    Task<byte[]> RetrievePhotoAsync(string photoPath, CancellationToken cancellationToken = default);
    Task DeletePhotoAsync(string photoPath, CancellationToken cancellationToken = default);
}
