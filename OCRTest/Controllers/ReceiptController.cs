using Microsoft.AspNetCore.Mvc;
using OCRTest.Models;
using OCRTest.Services;

namespace OCRTest.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReceiptController : ControllerBase
{
    private readonly IReceiptProcessingService _processingService;
    private readonly ILogger<ReceiptController> _logger;

    public ReceiptController(
        IReceiptProcessingService processingService,
        ILogger<ReceiptController> logger)
    {
        _processingService = processingService;
        _logger = logger;
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
}
