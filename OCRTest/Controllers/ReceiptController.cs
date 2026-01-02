using Microsoft.AspNetCore.Mvc;
using OCRTest.Models;
using OCRTest.Services;

namespace OCRTest.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReceiptController : ControllerBase
{
    private readonly IReceiptProcessingService _processingService;
    private readonly IReceiptStorageService? _storageService;
    private readonly ILogger<ReceiptController> _logger;

    public ReceiptController(
        IReceiptProcessingService processingService,
        ILogger<ReceiptController> logger,
        IReceiptStorageService? storageService = null)
    {
        _processingService = processingService;
        _logger = logger;
        _storageService = storageService;
    }

    [HttpPost("process/{filename}")]
    public async Task<ActionResult<ReceiptResponse>> ProcessReceipt(string filename, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing receipt request for file: {FileName}", filename);

        var result = await _processingService.ProcessReceiptAsync(filename, cancellationToken);

        if (!result.Success)
        {
            if (result.Error?.Code == "FileNotFound")
                return NotFound(result);

            if (result.Error?.Code == "RateLimitExceeded")
                return StatusCode(429, result);

            return StatusCode(503, result);
        }

        return Ok(result);
    }

    [HttpPost("process-all")]
    public async Task<ActionResult<BatchProcessingResponse>> ProcessAllReceipts(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing all receipts request");

        var result = await _processingService.ProcessAllReceiptsAsync(cancellationToken);

        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<object>> GetAvailableReceipts()
    {
        _logger.LogInformation("Getting available receipts");

        var receipts = await _processingService.GetAvailableReceiptsAsync();

        var response = new
        {
            receipts = receipts.Select(f => new
            {
                fileName = f.Name,
                fileSizeBytes = f.Length,
                lastModified = f.LastWriteTimeUtc
            }),
            totalCount = receipts.Count
        };

        return Ok(response);
    }

    // ============= New Database-Backed Endpoints =============

    [HttpPost("upload")]
    [RequestSizeLimit(10485760)] // 10 MB
    public async Task<ActionResult<ReceiptUploadResponse>> UploadReceipt(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing receipt upload: {FileName} ({FileSize} bytes)", file.FileName, file.Length);

        try
        {
            var result = await _processingService.ProcessUploadedReceiptAsync(file, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("validation failed"))
        {
            _logger.LogWarning("File validation failed: {Message}", ex.Message);
            return BadRequest(new { success = false, error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("rate limit"))
        {
            _logger.LogWarning("Rate limit exceeded");
            return StatusCode(429, new { success = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing uploaded receipt");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<object>> GetReceipt(Guid id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting receipt by ID: {ReceiptId}", id);

        try
        {
            var receipt = await _processingService.GetReceiptByIdAsync(id, cancellationToken);

            if (receipt == null)
            {
                _logger.LogWarning("Receipt not found: {ReceiptId}", id);
                return NotFound(new { success = false, error = $"Receipt with ID {id} not found" });
            }

            // Deserialize JSON fields if available
            ReceiptData? data = null;
            ConfidenceScores? confidence = null;

            if (!string.IsNullOrEmpty(receipt.ExtractedFieldsJson))
            {
                data = System.Text.Json.JsonSerializer.Deserialize<ReceiptData>(receipt.ExtractedFieldsJson);
            }

            if (!string.IsNullOrEmpty(receipt.ConfidenceScoresJson))
            {
                confidence = System.Text.Json.JsonSerializer.Deserialize<ConfidenceScores>(receipt.ConfidenceScoresJson);
            }

            var response = new
            {
                success = true,
                receiptId = receipt.Id,
                fileName = receipt.FileName,
                fileSizeBytes = receipt.FileSizeBytes,
                mimeType = receipt.MimeType,
                data = data,
                confidence = confidence,
                rawOcrText = receipt.RawOcrText,
                processingStatus = receipt.ProcessingStatus.ToString(),
                processingTimeMs = receipt.ProcessingTimeMs,
                errorMessage = receipt.ErrorMessage,
                createdAt = receipt.CreatedAt,
                processedAt = receipt.ProcessedAt
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving receipt {ReceiptId}", id);
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    [HttpGet("{id:guid}/photo")]
    public async Task<IActionResult> GetReceiptPhoto(Guid id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting photo for receipt: {ReceiptId}", id);

        if (_storageService == null)
        {
            _logger.LogError("Storage service not configured");
            return StatusCode(500, new { success = false, error = "Storage service not configured" });
        }

        try
        {
            var receipt = await _processingService.GetReceiptByIdAsync(id, cancellationToken);

            if (receipt == null)
            {
                _logger.LogWarning("Receipt not found: {ReceiptId}", id);
                return NotFound(new { success = false, error = $"Receipt with ID {id} not found" });
            }

            if (string.IsNullOrEmpty(receipt.PhotoPath))
            {
                _logger.LogWarning("Photo path not found for receipt: {ReceiptId}", id);
                return NotFound(new { success = false, error = "Photo not found for this receipt" });
            }

            var photoBytes = await _storageService.RetrievePhotoAsync(receipt.PhotoPath, cancellationToken);

            return File(photoBytes, receipt.MimeType, receipt.FileName);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogError(ex, "Photo file not found for receipt {ReceiptId}", id);
            return NotFound(new { success = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving photo for receipt {ReceiptId}", id);
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    [HttpGet("history")]
    public async Task<ActionResult<PagedReceiptResponse>> GetReceiptHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting receipt history: page={Page}, pageSize={PageSize}, status={Status}",
            page, pageSize, status ?? "all");

        try
        {
            // Parse status if provided
            ProcessingStatus? processingStatus = null;
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<ProcessingStatus>(status, true, out var parsedStatus))
            {
                processingStatus = parsedStatus;
            }

            // Validate pagination parameters
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100; // Max page size

            var result = await _processingService.GetReceiptsPagedAsync(page, pageSize, processingStatus, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving receipt history");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }
}
