using OCRTest.Models;

namespace OCRTest.Services;

public class FileValidationService : IFileValidationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileValidationService> _logger;
    private readonly string[] _allowedExtensions;
    private readonly long _maxFileSizeBytes;
    private readonly Dictionary<string, string> _mimeTypeMap;

    public FileValidationService(
        IConfiguration configuration,
        ILogger<FileValidationService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        _allowedExtensions = _configuration.GetSection("ReceiptProcessing:SupportedExtensions")
            .Get<string[]>() ?? new[] { ".jpg", ".jpeg", ".png", ".pdf" };

        var maxFileSizeMB = _configuration.GetValue<int>("ReceiptProcessing:MaxFileSizeMB", 10);
        _maxFileSizeBytes = maxFileSizeMB * 1024 * 1024;

        _mimeTypeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".jpg", "image/jpeg" },
            { ".jpeg", "image/jpeg" },
            { ".png", "image/png" },
            { ".pdf", "application/pdf" }
        };
    }

    public async Task<FileValidationResult> ValidateUploadAsync(IFormFile file)
    {
        var result = new FileValidationResult { IsValid = true };

        if (file == null || file.Length == 0)
        {
            result.IsValid = false;
            result.Errors.Add("El archivo está vacío o no fue proporcionado");
            return result;
        }

        // Validate file size
        if (file.Length > _maxFileSizeBytes)
        {
            result.IsValid = false;
            result.Errors.Add($"El archivo excede el tamaño máximo permitido de {_maxFileSizeBytes / (1024 * 1024)} MB");
            _logger.LogWarning("File size validation failed: {FileName} ({FileSize} bytes)",
                file.FileName, file.Length);
        }

        // Validate extension
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension) || !_allowedExtensions.Contains(extension))
        {
            result.IsValid = false;
            result.Errors.Add($"Extensión de archivo no permitida. Extensiones permitidas: {string.Join(", ", _allowedExtensions)}");
            _logger.LogWarning("File extension validation failed: {FileName} (extension: {Extension})",
                file.FileName, extension);
        }

        // Validate MIME type by reading file content
        try
        {
            var detectedMimeType = await DetectMimeTypeAsync(file);
            result.MimeType = detectedMimeType;

            if (_mimeTypeMap.TryGetValue(extension, out var expectedMimeType))
            {
                if (!string.Equals(detectedMimeType, expectedMimeType, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(detectedMimeType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "MIME type mismatch for {FileName}: expected {Expected}, detected {Detected}",
                        file.FileName, expectedMimeType, detectedMimeType);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting MIME type for {FileName}", file.FileName);
        }

        if (result.IsValid)
        {
            _logger.LogInformation("File validation passed: {FileName} ({FileSize} bytes)",
                file.FileName, file.Length);
        }

        return result;
    }

    private async Task<string> DetectMimeTypeAsync(IFormFile file)
    {
        // Detect MIME type based on file signature (magic numbers)
        using var stream = file.OpenReadStream();
        var buffer = new byte[8];
        await stream.ReadAsync(buffer, 0, buffer.Length);
        stream.Position = 0;

        // JPEG: FF D8 FF
        if (buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF)
            return "image/jpeg";

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
            return "image/png";

        // PDF: 25 50 44 46 (%PDF)
        if (buffer[0] == 0x25 && buffer[1] == 0x50 && buffer[2] == 0x44 && buffer[3] == 0x46)
            return "application/pdf";

        // Fallback to content type from HTTP request
        return file.ContentType ?? "application/octet-stream";
    }
}
