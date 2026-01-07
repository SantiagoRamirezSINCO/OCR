using Microsoft.AspNetCore.Mvc;

namespace OCRTest.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly ILogger<HealthController> _logger;

    public HealthController(ILogger<HealthController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public ActionResult<object> GetHealth()
    {
        _logger.LogInformation("Health check requested");

        var response = new
        {
            status = "Healthy",
            service = "OCR Receipt Processing API",
            timestamp = DateTime.UtcNow
        };

        _logger.LogInformation("Health check completed: {Status}", response.status);

        return Ok(response);
    }
}
