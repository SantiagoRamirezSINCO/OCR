using Azure.AI.FormRecognizer.DocumentAnalysis;

namespace OCRTest.Services;

public interface IAzureDocumentIntelligenceService
{
    Task<AnalyzeResult> AnalyzeReceiptAsync(Stream documentStream, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync();
}
