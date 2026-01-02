using OCRTest.Models;

namespace OCRTest.Services;

public interface IReceiptRepository
{
    Task<ReceiptEntity> CreateAsync(ReceiptEntity receipt, CancellationToken cancellationToken = default);
    Task<ReceiptEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<(List<ReceiptEntity> Items, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        ProcessingStatus? status = null,
        CancellationToken cancellationToken = default);
    Task UpdateAsync(ReceiptEntity receipt, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
