using OCRTest.Models;

namespace OCRTest.Services;

public interface IFileValidationService
{
    Task<FileValidationResult> ValidateUploadAsync(IFormFile file);
}
