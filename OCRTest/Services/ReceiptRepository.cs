using Microsoft.EntityFrameworkCore;
using OCRTest.Data;
using OCRTest.Models;

namespace OCRTest.Services;

public class ReceiptRepository : IReceiptRepository
{
    private readonly ReceiptDbContext _context;
    private readonly ILogger<ReceiptRepository> _logger;

    public ReceiptRepository(
        ReceiptDbContext context,
        ILogger<ReceiptRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ReceiptEntity> CreateAsync(ReceiptEntity receipt, CancellationToken cancellationToken = default)
    {
        try
        {
            receipt.CreatedAt = DateTime.UtcNow;
            receipt.UpdatedAt = DateTime.UtcNow;

            await _context.Receipts.AddAsync(receipt, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Created receipt entity with ID: {ReceiptId}", receipt.Id);
            return receipt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating receipt entity");
            throw;
        }
    }

    public async Task<ReceiptEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var receipt = await _context.Receipts
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (receipt == null)
            {
                _logger.LogWarning("Receipt not found with ID: {ReceiptId}", id);
            }

            return receipt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving receipt with ID: {ReceiptId}", id);
            throw;
        }
    }

    public async Task<(List<ReceiptEntity> Items, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        ProcessingStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.Receipts.AsNoTracking();

            // Apply filter by status if provided
            if (status.HasValue)
            {
                query = query.Where(r => r.ProcessingStatus == status.Value);
            }

            // Get total count
            var totalCount = await query.CountAsync(cancellationToken);

            // Apply pagination
            var items = await query
                .OrderByDescending(r => r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            _logger.LogInformation(
                "Retrieved page {Page} of receipts (page size: {PageSize}, total: {TotalCount}, status filter: {Status})",
                page, pageSize, totalCount, status?.ToString() ?? "none");

            return (items, totalCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving paged receipts");
            throw;
        }
    }

    public async Task UpdateAsync(ReceiptEntity receipt, CancellationToken cancellationToken = default)
    {
        try
        {
            receipt.UpdatedAt = DateTime.UtcNow;

            _context.Receipts.Update(receipt);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Updated receipt entity with ID: {ReceiptId}", receipt.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating receipt with ID: {ReceiptId}", receipt.Id);
            throw;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var receipt = await _context.Receipts
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (receipt != null)
            {
                _context.Receipts.Remove(receipt);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted receipt entity with ID: {ReceiptId}", id);
            }
            else
            {
                _logger.LogWarning("Attempted to delete non-existent receipt with ID: {ReceiptId}", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting receipt with ID: {ReceiptId}", id);
            throw;
        }
    }
}
