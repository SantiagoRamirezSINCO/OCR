using System.Net;
using System.Text.Json;
using Azure;
using OCRTest.Models;

namespace OCRTest.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "File not found: {Message}", ex.Message);
            await HandleExceptionAsync(context, HttpStatusCode.NotFound, "FileNotFound", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogWarning(ex, "Directory not found: {Message}", ex.Message);
            await HandleExceptionAsync(context, HttpStatusCode.InternalServerError, "DirectoryNotFound", ex.Message);
        }
        catch (RequestFailedException ex) when (ex.Status == 429)
        {
            _logger.LogWarning(ex, "Azure rate limit exceeded");
            await HandleExceptionAsync(context, HttpStatusCode.TooManyRequests, "RateLimitExceeded",
                "Azure F0 tier rate limit exceeded. Please wait 60 seconds before retrying.");
        }
        catch (RequestFailedException ex) when (ex.Status == 401)
        {
            _logger.LogError(ex, "Azure authentication failed");
            await HandleExceptionAsync(context, HttpStatusCode.ServiceUnavailable, "AzureAuthenticationFailed",
                "Azure Document Intelligence authentication failed. Please verify your API key and endpoint in appsettings.json");
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Azure service error: Status {Status}", ex.Status);
            await HandleExceptionAsync(context, HttpStatusCode.ServiceUnavailable, "AzureServiceError",
                $"Azure Document Intelligence service error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception: {Message}", ex.Message);
            await HandleExceptionAsync(context, HttpStatusCode.InternalServerError, "InternalServerError",
                "An unexpected error occurred while processing your request.");
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, HttpStatusCode statusCode, string errorCode, string message)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        var error = new
        {
            success = false,
            error = new ProcessingError
            {
                Code = errorCode,
                Message = message
            },
            timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(error, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        await context.Response.WriteAsync(json);
    }
}
