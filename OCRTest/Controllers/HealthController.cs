using Microsoft.AspNetCore.Mvc;
using OCRTest.Services;

namespace OCRTest.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IAzureDocumentIntelligenceService _azureService;
    private readonly IReceiptProcessingService _processingService;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        IAzureDocumentIntelligenceService azureService,
        IReceiptProcessingService processingService,
        ILogger<HealthController> logger)
    {
        _azureService = azureService;
        _processingService = processingService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<object>> GetHealth()
    {
        _logger.LogInformation("Health check requested");

        var receipts = await _processingService.GetAvailableReceiptsAsync();
        var receiptsAccessible = receipts.Any();

        var response = new
        {
            status = "Healthy",
            azureConnection = "Unknown",
            receiptsFolder = receiptsAccessible ? "Accessible" : "Not Accessible",
            receiptCount = receipts.Count,
            timestamp = DateTime.UtcNow
        };

        _logger.LogInformation("Health check completed: {Status}", response.status);

        return Ok(response);
    }
}
