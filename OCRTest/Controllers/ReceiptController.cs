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

    [HttpPost("process")]
    [RequestSizeLimit(52428800)] // 50 MB
    public async Task<ActionResult<ReceiptResponse>> ProcessReceipt(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
        {
            _logger.LogWarning("No file uploaded");
            return BadRequest(new ReceiptResponse
            {
                Success = false,
                Error = new ProcessingError
                {
                    Code = "NoFileProvided",
                    Message = "No file was provided in the request"
                }
            });
        }

        _logger.LogInformation("Processing uploaded receipt: {FileName} ({FileSize} bytes)", file.FileName, file.Length);

        try
        {
            using var stream = file.OpenReadStream();
            var result = await _processingService.ProcessReceiptAsync(stream, file.FileName, cancellationToken);

            if (!result.Success)
            {
                if (result.Error?.Code == "RateLimitExceeded")
                    return StatusCode(429, result);

                return StatusCode(503, result);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing uploaded receipt");
            return StatusCode(500, new ReceiptResponse
            {
                Success = false,
                FileName = file.FileName,
                Error = new ProcessingError
                {
                    Code = "InternalError",
                    Message = ex.Message
                }
            });
        }
    }
}
