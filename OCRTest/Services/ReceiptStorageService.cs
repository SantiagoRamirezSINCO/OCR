namespace OCRTest.Services;

public class ReceiptStorageService : IReceiptStorageService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReceiptStorageService> _logger;
    private readonly string _storageBasePath;

    public ReceiptStorageService(
        IConfiguration configuration,
        ILogger<ReceiptStorageService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        _storageBasePath = _configuration["ReceiptProcessing:ReceiptsFolderPath"]
            ?? "/app/data/receipts";

        // Ensure base path exists
        if (!Directory.Exists(_storageBasePath))
        {
            Directory.CreateDirectory(_storageBasePath);
            _logger.LogInformation("Created storage base path: {Path}", _storageBasePath);
        }
    }

    public async Task<string> SavePhotoAsync(IFormFile file, Guid receiptId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Create directory for this receipt: {basePath}/{receiptId}/
            var receiptDirectory = Path.Combine(_storageBasePath, receiptId.ToString());
            if (!Directory.Exists(receiptDirectory))
            {
                Directory.CreateDirectory(receiptDirectory);
                _logger.LogInformation("Created receipt directory: {Directory}", receiptDirectory);
            }

            // Save file with sanitized filename
            var sanitizedFileName = SanitizeFileName(file.FileName);
            var filePath = Path.Combine(receiptDirectory, sanitizedFileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(fileStream, cancellationToken);
            }

            // Return relative path for storage in database
            var relativePath = Path.Combine(receiptId.ToString(), sanitizedFileName);
            _logger.LogInformation("Saved photo to: {Path} ({Size} bytes)", relativePath, file.Length);

            return relativePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving photo for receipt {ReceiptId}", receiptId);
            throw;
        }
    }

    public async Task<byte[]> RetrievePhotoAsync(string photoPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.Combine(_storageBasePath, photoPath);

            if (!File.Exists(fullPath))
            {
                _logger.LogError("Photo not found at path: {Path}", fullPath);
                throw new FileNotFoundException($"Photo not found: {photoPath}");
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            _logger.LogInformation("Retrieved photo from: {Path} ({Size} bytes)", photoPath, bytes.Length);

            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving photo from path: {Path}", photoPath);
            throw;
        }
    }

    public async Task DeletePhotoAsync(string photoPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.Combine(_storageBasePath, photoPath);

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                _logger.LogInformation("Deleted photo at: {Path}", photoPath);

                // Try to delete the directory if it's empty
                var directory = Path.GetDirectoryName(fullPath);
                if (directory != null && Directory.Exists(directory))
                {
                    var filesInDirectory = Directory.GetFiles(directory);
                    if (filesInDirectory.Length == 0)
                    {
                        Directory.Delete(directory);
                        _logger.LogInformation("Deleted empty directory: {Directory}", directory);
                    }
                }
            }
            else
            {
                _logger.LogWarning("Attempted to delete non-existent photo: {Path}", photoPath);
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting photo at path: {Path}", photoPath);
            throw;
        }
    }

    private string SanitizeFileName(string fileName)
    {
        // Remove invalid file name characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

        // Ensure filename isn't too long (max 255 characters)
        if (sanitized.Length > 255)
        {
            var extension = Path.GetExtension(sanitized);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = nameWithoutExt.Substring(0, 255 - extension.Length) + extension;
        }

        return sanitized;
    }
}
