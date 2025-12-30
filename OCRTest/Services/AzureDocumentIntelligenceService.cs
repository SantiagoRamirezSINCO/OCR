using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Microsoft.Extensions.Options;
using OCRTest.Models;

namespace OCRTest.Services;

public class AzureDocumentIntelligenceService : IAzureDocumentIntelligenceService
{
    private readonly DocumentAnalysisClient _client;
    private readonly ILogger<AzureDocumentIntelligenceService> _logger;
    private readonly string _modelId;
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;

    public AzureDocumentIntelligenceService(
        IOptions<AzureDocumentIntelligenceOptions> options,
        ILogger<AzureDocumentIntelligenceService> logger)
    {
        var config = options.Value;
        _logger = logger;
        _modelId = config.ModelId;

        if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _logger.LogWarning("Azure Document Intelligence not configured. Please update appsettings.json with your credentials.");
        }

        var credential = new AzureKeyCredential(config.ApiKey);
        _client = new DocumentAnalysisClient(new Uri(config.Endpoint), credential);
    }

    public async Task<AnalyzeResult> AnalyzeReceiptAsync(Stream documentStream, CancellationToken cancellationToken = default)
    {
        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            // F0 tier: 1 request per minute - enforce rate limiting
            var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
            if (timeSinceLastRequest < TimeSpan.FromSeconds(60))
            {
                var delay = TimeSpan.FromSeconds(60) - timeSinceLastRequest;
                _logger.LogInformation("Rate limiting: waiting {Seconds}s to comply with F0 tier limits", delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }

            _logger.LogInformation("Sending document to Azure Document Intelligence for analysis with model {ModelId}", _modelId);

            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                _modelId,
                documentStream,
                cancellationToken: cancellationToken);

            _lastRequestTime = DateTime.UtcNow;
            _logger.LogInformation("Document analysis completed successfully");

            return operation.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 429)
        {
            _logger.LogWarning("Rate limit exceeded (429). F0 tier allows 1 request per minute.");
            throw;
        }
        catch (RequestFailedException ex) when (ex.Status == 401)
        {
            _logger.LogError("Authentication failed (401). Please verify your API key and endpoint in appsettings.json");
            throw;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Azure Document Intelligence request failed with status {Status}", ex.Status);
            throw;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            using var testStream = new MemoryStream(new byte[1]);
            await _client.AnalyzeDocumentAsync(WaitUntil.Started, _modelId, testStream);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
