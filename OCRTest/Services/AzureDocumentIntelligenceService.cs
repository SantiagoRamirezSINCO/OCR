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
    private readonly AzureDocumentIntelligenceOptions _options;
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private static readonly Queue<DateTime> _requestTimestamps = new();
    private static readonly object _timestampLock = new object();

    public AzureDocumentIntelligenceService(
        IOptions<AzureDocumentIntelligenceOptions> options,
        ILogger<AzureDocumentIntelligenceService> logger)
    {
        var config = options.Value;
        _options = config;
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
            // Apply rate limiting based on tier configuration
            if (_options.EnableRateLimiting)
            {
                var delay = CalculateRateLimitDelay();
                if (delay > TimeSpan.Zero)
                {
                    _logger.LogInformation(
                        "Rate limiting: waiting {Seconds}s to comply with {Tier} tier limits ({MaxRequests} requests per {WindowSeconds}s)",
                        delay.TotalSeconds,
                        _options.Tier,
                        _options.MaxRequestsPerWindow,
                        _options.RateLimitWindowSeconds);
                    await Task.Delay(delay, cancellationToken);
                }
            }

            _logger.LogInformation("Sending document to Azure Document Intelligence for analysis with model {ModelId}", _modelId);

            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                _modelId,
                documentStream,
                cancellationToken: cancellationToken);

            // Track this request timestamp
            if (_options.EnableRateLimiting)
            {
                TrackRequest();
            }

            _logger.LogInformation("Document analysis completed successfully");

            return operation.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 429)
        {
            _logger.LogWarning(
                "Rate limit exceeded (429). {Tier} tier allows {MaxRequests} requests per {WindowSeconds} seconds.",
                _options.Tier,
                _options.MaxRequestsPerWindow,
                _options.RateLimitWindowSeconds);
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

    private TimeSpan CalculateRateLimitDelay()
    {
        lock (_timestampLock)
        {
            var now = DateTime.UtcNow;
            var windowStart = now.AddSeconds(-_options.RateLimitWindowSeconds);

            // Remove timestamps outside the current window
            while (_requestTimestamps.Count > 0 && _requestTimestamps.Peek() < windowStart)
            {
                _requestTimestamps.Dequeue();
            }

            // Check if we're at capacity
            if (_requestTimestamps.Count >= _options.MaxRequestsPerWindow)
            {
                var oldestRequest = _requestTimestamps.Peek();
                var delay = oldestRequest.AddSeconds(_options.RateLimitWindowSeconds) - now;
                return delay + TimeSpan.FromMilliseconds(100);
            }

            return TimeSpan.Zero;
        }
    }

    private void TrackRequest()
    {
        lock (_timestampLock)
        {
            _requestTimestamps.Enqueue(DateTime.UtcNow);

            var maxTrackedRequests = _options.MaxRequestsPerWindow * 2;
            while (_requestTimestamps.Count > maxTrackedRequests)
            {
                _requestTimestamps.Dequeue();
            }
        }
    }
}
