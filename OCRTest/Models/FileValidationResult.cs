namespace OCRTest.Models;

public class FileValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? MimeType { get; set; }
}
