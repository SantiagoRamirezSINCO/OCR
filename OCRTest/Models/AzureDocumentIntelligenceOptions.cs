namespace OCRTest.Models;

public class AzureDocumentIntelligenceOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "prebuilt-receipt";

    /// <summary>
    /// Maximum number of requests allowed per time window.
    /// - F0 (Free): 1
    /// - S0 (Standard): 15
    /// </summary>
    public int MaxRequestsPerWindow { get; set; } = 1;

    /// <summary>
    /// Time window duration in seconds for rate limiting.
    /// - F0 (Free): 60 seconds
    /// - S0 (Standard): 1 second
    /// </summary>
    public int RateLimitWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Tier name for logging purposes (F0, S0, etc.)
    /// </summary>
    public string Tier { get; set; } = "F0";

    /// <summary>
    /// Enable rate limiting enforcement.
    /// </summary>
    public bool EnableRateLimiting { get; set; } = true;
}
