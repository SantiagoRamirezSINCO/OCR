using System.Diagnostics;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Microsoft.Extensions.Options;
using OCRTest.Models;

namespace OCRTest.Services;

public class ReceiptProcessingService : IReceiptProcessingService
{
    private readonly IAzureDocumentIntelligenceService _azureService;
    private readonly ILogger<ReceiptProcessingService> _logger;
    private readonly string _receiptsFolderPath;
    private readonly string[] _supportedExtensions;

    public ReceiptProcessingService(
        IAzureDocumentIntelligenceService azureService,
        ILogger<ReceiptProcessingService> logger,
        IConfiguration configuration)
    {
        _azureService = azureService;
        _logger = logger;
        _receiptsFolderPath = configuration["ReceiptProcessing:ReceiptsFolderPath"] ?? "exampleReceipts";
        _supportedExtensions = configuration.GetSection("ReceiptProcessing:SupportedExtensions").Get<string[]>()
            ?? new[] { ".jpg", ".jpeg", ".png", ".pdf" };
    }

    public async Task<ReceiptResponse> ProcessReceiptAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = new ReceiptResponse { FileName = fileName };

        try
        {
            var filePath = Path.Combine(_receiptsFolderPath, fileName);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Receipt file not found: {FilePath}", filePath);
                response.Success = false;
                response.Error = new ProcessingError
                {
                    Code = "FileNotFound",
                    Message = $"Receipt file '{fileName}' not found in {_receiptsFolderPath} folder"
                };
                return response;
            }

            _logger.LogInformation("Processing receipt: {FileName}", fileName);

            using var fileStream = File.OpenRead(filePath);
            var analyzeResult = await _azureService.AnalyzeReceiptAsync(fileStream, cancellationToken);

            var (data, confidence) = ExtractReceiptData(analyzeResult);

            response.Success = true;
            response.Data = data;
            response.Confidence = confidence;
            response.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

            _logger.LogInformation("Successfully processed receipt: {FileName} in {Ms}ms", fileName, stopwatch.ElapsedMilliseconds);
        }
        catch (RequestFailedException ex) when (ex.Status == 429)
        {
            _logger.LogWarning("Rate limit exceeded while processing {FileName}", fileName);
            response.Success = false;
            response.Error = new ProcessingError
            {
                Code = "RateLimitExceeded",
                Message = "Azure F0 tier rate limit exceeded. Please wait 60 seconds before retrying."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing receipt {FileName}", fileName);
            response.Success = false;
            response.Error = new ProcessingError
            {
                Code = "ProcessingError",
                Message = ex.Message
            };
        }

        return response;
    }

    public async Task<BatchProcessingResponse> ProcessAllReceiptsAsync(CancellationToken cancellationToken = default)
    {
        var response = new BatchProcessingResponse();
        var receipts = await GetAvailableReceiptsAsync();

        _logger.LogInformation("Starting batch processing of {Count} receipts", receipts.Count);

        foreach (var receipt in receipts)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Batch processing cancelled");
                break;
            }

            var result = await ProcessReceiptAsync(receipt.Name, cancellationToken);
            response.Results.Add(result);
            response.TotalProcessed++;

            if (result.Success)
                response.Successful++;
            else
                response.Failed++;
        }

        _logger.LogInformation("Batch processing completed: {Total} total, {Successful} successful, {Failed} failed",
            response.TotalProcessed, response.Successful, response.Failed);

        return response;
    }

    public Task<List<FileInfo>> GetAvailableReceiptsAsync()
    {
        var receipts = new List<FileInfo>();

        if (!Directory.Exists(_receiptsFolderPath))
        {
            _logger.LogWarning("Receipts folder not found: {Path}", _receiptsFolderPath);
            return Task.FromResult(receipts);
        }

        var files = Directory.GetFiles(_receiptsFolderPath)
            .Where(f => _supportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name)
            .ToList();

        _logger.LogInformation("Found {Count} receipt files in {Path}", files.Count, _receiptsFolderPath);

        return Task.FromResult(files);
    }

    private (ReceiptData data, ConfidenceScores confidence) ExtractReceiptData(AnalyzeResult result)
    {
        var data = new ReceiptData();
        var confidence = new ConfidenceScores();

        // Extract standard fields from prebuilt receipt model
        if (result.Documents?.Count > 0)
        {
            var document = result.Documents[0];

            // Extract Merchant Name
            if (document.Fields.TryGetValue("MerchantName", out var merchantField) && merchantField != null)
            {
                data.NombreDeLaGasolinera = merchantField.Content;
                confidence.NombreDeLaGasolinera = merchantField.Confidence ?? 0;
                _logger.LogDebug("Extracted MerchantName: {Value} (confidence: {Confidence})",
                    data.NombreDeLaGasolinera, confidence.NombreDeLaGasolinera);
            }

            // Extract Total
            if (document.Fields.TryGetValue("Total", out var totalField) && totalField != null)
            {
                if (totalField.FieldType == DocumentFieldType.Currency)
                {
                    data.Total = totalField.Value?.AsCurrency().Amount;
                    confidence.Total = totalField.Confidence ?? 0;
                    _logger.LogDebug("Extracted Total: {Value} (confidence: {Confidence})",
                        data.Total, confidence.Total);
                }
                else if (totalField.FieldType == DocumentFieldType.Double)
                {
                    data.Total = totalField.Value?.AsDouble();
                    confidence.Total = totalField.Confidence ?? 0;
                }
            }

            // Extract TransactionDate from Azure (Fecha de tanqueo)
            if (document.Fields.TryGetValue("TransactionDate", out var dateField) && dateField != null)
            {
                if (dateField.FieldType == DocumentFieldType.Date)
                {
                    var dateOffset = dateField.Value?.AsDate();
                    data.FechaDeTanqueo = dateOffset?.DateTime;
                    confidence.FechaDeTanqueo = dateField.Confidence ?? 0;
                    _logger.LogDebug("Extracted TransactionDate: {Value} (confidence: {Confidence})",
                        data.FechaDeTanqueo, confidence.FechaDeTanqueo);
                }
            }
        }

        // Extract all OCR text for custom field extraction
        var allText = string.Join(" ", result.Pages
            .SelectMany(p => p.Lines ?? Enumerable.Empty<DocumentLine>())
            .Select(l => l.Content));

        _logger.LogDebug("Full OCR text: {Text}", allText);

        // Extract Placa using regex patterns
        var (placa, placaConfidence) = ExtractPlaca(allText);
        data.Placa = placa;
        confidence.Placa = placaConfidence;

        if (!string.IsNullOrEmpty(placa))
        {
            _logger.LogDebug("Extracted Placa: {Value} (confidence: {Confidence})", placa, placaConfidence);
        }
        else
        {
            _logger.LogWarning("Could not extract Placa from receipt");
        }

        // Fallback: Extract Fecha de tanqueo via regex if not from Azure
        if (data.FechaDeTanqueo == null)
        {
            var (fecha, fechaConfidence) = ExtractFechaDeTanqueo(allText);
            data.FechaDeTanqueo = fecha;
            confidence.FechaDeTanqueo = fechaConfidence;

            if (fecha != null)
            {
                _logger.LogDebug("Extracted Fecha de tanqueo via regex: {Value} (confidence: {Confidence})",
                    fecha, fechaConfidence);
            }
        }

        // Extract Cantidad (Galones)
        var (cantidad, cantidadConfidence) = ExtractCantidad(allText);
        data.Cantidad = cantidad;
        confidence.Cantidad = cantidadConfidence;

        if (cantidad != null)
        {
            _logger.LogDebug("Extracted Cantidad: {Value} Gal (confidence: {Confidence})",
                cantidad, cantidadConfidence);
        }
        else
        {
            _logger.LogWarning("Could not extract Cantidad from receipt");
        }

        // Extract Kilometraje
        var (kilometraje, kmConfidence) = ExtractKilometraje(allText);
        data.Kilometraje = kilometraje;
        confidence.Kilometraje = kmConfidence;

        if (kilometraje != null)
        {
            _logger.LogDebug("Extracted Kilometraje: {Value} KM (confidence: {Confidence})",
                kilometraje, kmConfidence);
        }
        else
        {
            _logger.LogWarning("Could not extract Kilometraje from receipt");
        }

        // Extract Número de vale
        var (numeroVale, valeConfidence) = ExtractNumeroDeVale(allText);
        data.NumeroDeVale = numeroVale;
        confidence.NumeroDeVale = valeConfidence;

        if (!string.IsNullOrEmpty(numeroVale))
        {
            _logger.LogDebug("Extracted Numero de vale: {Value} (confidence: {Confidence})",
                numeroVale, valeConfidence);
        }
        else
        {
            _logger.LogWarning("Could not extract Numero de vale from receipt");
        }

        return (data, confidence);
    }

    private (string? placa, double confidence) ExtractPlaca(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // Pattern 1: "Placa: ABC123" or "Placa ABC123" (highest confidence)
        var pattern1 = new Regex(@"Placa[:\s]+([A-Z]{3}[-]?\d{3,4})", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success)
        {
            var placa = match1.Groups[1].Value.ToUpperInvariant();
            _logger.LogDebug("Placa matched with pattern 1 (Placa:): {Placa}", placa);
            return (placa, 0.9);
        }

        // Pattern 2: "PLACA ABC123" (high confidence)
        var pattern2 = new Regex(@"PLACA\s+([A-Z]{3}[-]?\d{3,4})", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success)
        {
            var placa = match2.Groups[1].Value.ToUpperInvariant();
            _logger.LogDebug("Placa matched with pattern 2 (PLACA): {Placa}", placa);
            return (placa, 0.85);
        }

        // Pattern 3: Generic license plate format ABC123 or ABC-123 (lower confidence)
        var pattern3 = new Regex(@"\b([A-Z]{3}[-]?\d{3,4})\b");
        var match3 = pattern3.Match(text);
        if (match3.Success)
        {
            var placa = match3.Groups[1].Value.ToUpperInvariant();
            _logger.LogDebug("Placa matched with pattern 3 (generic): {Placa}", placa);
            return (placa, 0.6);
        }

        _logger.LogDebug("No Placa pattern matched in text");
        return (null, 0);
    }

    private (DateTime? fecha, double confidence) ExtractFechaDeTanqueo(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // Pattern 1: "Fecha: 2024-12-15" o "Fecha 2024-12-15" (ISO format - highest confidence)
        var pattern1 = new Regex(@"Fecha[:\s]+(20\d{2}[-/]\d{2}[-/]\d{2})", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success && DateTime.TryParse(match1.Groups[1].Value, out var fecha1))
        {
            _logger.LogDebug("Fecha matched with pattern 1 (Fecha: ISO): {Fecha}", fecha1);
            return (fecha1, 0.9);
        }

        // Pattern 2: Standalone ISO format 2024-12-15 (high confidence)
        var pattern2 = new Regex(@"\b(20\d{2}[-/]\d{2}[-/]\d{2})\b");
        var match2 = pattern2.Match(text);
        if (match2.Success && DateTime.TryParse(match2.Groups[1].Value, out var fecha2))
        {
            _logger.LogDebug("Fecha matched with pattern 2 (ISO): {Fecha}", fecha2);
            return (fecha2, 0.85);
        }

        // Pattern 3: Generic date DD/MM/YYYY or DD-MM-YYYY (lower confidence)
        var pattern3 = new Regex(@"\b(\d{1,2}[-/]\d{1,2}[-/]\d{2,4})\b");
        var match3 = pattern3.Match(text);
        if (match3.Success && DateTime.TryParse(match3.Groups[1].Value, out var fecha3))
        {
            _logger.LogDebug("Fecha matched with pattern 3 (generic): {Fecha}", fecha3);
            return (fecha3, 0.6);
        }

        _logger.LogDebug("No Fecha pattern matched in text");
        return (null, 0);
    }

    private (double? cantidad, double confidence) ExtractCantidad(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // Pattern 1: "Cantidad: 15.5 Gal" o "Volumen: 5.334" (highest confidence)
        var pattern1 = new Regex(@"(?:Cantidad|Volumen|Galones)[:\s]+([\d,.]+)\s*(?:Gal|Galones)?", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success && TryParseDecimal(match1.Groups[1].Value, out var cantidad1))
        {
            _logger.LogDebug("Cantidad matched with pattern 1 (Cantidad/Volumen:): {Cantidad}", cantidad1);
            return (cantidad1, 0.9);
        }

        // Pattern 2: "GAL :G 10.366" format (high confidence)
        var pattern2 = new Regex(@"GAL\s*:G\s*([\d,.]+)", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success && TryParseDecimal(match2.Groups[1].Value, out var cantidad2))
        {
            _logger.LogDebug("Cantidad matched with pattern 2 (GAL :G): {Cantidad}", cantidad2);
            return (cantidad2, 0.88);
        }
        
        // Pattern 3: Near fuel type "Corriente: 15.5 Gal" (medium confidence)
        var pattern4 = new Regex(@"(?:Corriente|Extra|Diesel)[:\s]+([\d,.]+)\s*(?:Gal|Galones)", RegexOptions.IgnoreCase);
        var match4 = pattern4.Match(text);
        if (match4.Success && TryParseDecimal(match4.Groups[1].Value, out var cantidad4))
        {
            _logger.LogDebug("Cantidad matched with pattern 4 (fuel type): {Cantidad}", cantidad4);
            return (cantidad4, 0.8);
        }

        _logger.LogDebug("No Cantidad pattern matched in text");
        return (null, 0);
    }

    private (int? kilometraje, double confidence) ExtractKilometraje(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // Pattern 1: "Kilometraje: 125000" o "Kilometros: 23453" o "Odómetro: 125000" (highest confidence)
        var pattern1 = new Regex(@"(?:Kilometraje|Kilometros|Odometro|Odómetro)[:\s]+(\d{1,7})(?:\s*KM)?", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success && int.TryParse(match1.Groups[1].Value, out var km1))
        {
            _logger.LogDebug("Kilometraje matched with pattern 1 (Kilometraje/Kilometros/Odómetro:): {Km}", km1);
            return (km1, 0.9);
        }

        // Pattern 2: "KM: 125000" or "KM 125000" (high confidence)
        var pattern2 = new Regex(@"\bKM[:\s]+(\d{5,7})\b", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success && int.TryParse(match2.Groups[1].Value, out var km2))
        {
            _logger.LogDebug("Kilometraje matched with pattern 2 (KM:): {Km}", km2);
            return (km2, 0.85);
        }

        _logger.LogDebug("No Kilometraje pattern matched in text");
        return (null, 0);
    }

    private (string? numeroVale, double confidence) ExtractNumeroDeVale(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // Pattern 1: "ORDEN DE VENTA: 4894" or "ORDEN DE PEDIDO: 123" - skips letter-only words (highest confidence)
        var pattern1 = new Regex(@"ORDEN\s+DE\s+(?:VENTA|PEDIDO)[:\s]+(?:[A-Za-z]+[ \t]+)*(\S+)", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success)
        {
            var vale = match1.Groups[1].Value.ToUpperInvariant();
            // Validate that it contains at least one alphanumeric character (skip ":" or special chars only)
            if (vale.Any(char.IsLetterOrDigit))
            {
                _logger.LogDebug("Numero de vale matched with pattern 1 (ORDEN DE VENTA/PEDIDO:): {Vale}", vale);
                return (vale, 0.95);
            }
        }

        // Pattern 2: "Numero: OP1-51928" or "Vale: ABC123" - skips letter-only prefixes (high confidence)
        var pattern2 = new Regex(@"(?:Vale|Recibo|Factura|Numero)[:\s#]+(?:[A-Za-z]+[ \t]+)*(\S+)", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success)
        {
            var vale = match2.Groups[1].Value.ToUpperInvariant();
            if (vale.Any(char.IsLetterOrDigit))
            {
                _logger.LogDebug("Numero de vale matched with pattern 2 (Vale/Recibo/Factura/Numero:): {Vale}", vale);
                return (vale, 0.9);
            }
        }

        // Pattern 3: "No. RESH 81651653" - skips letter-only prefix like RESH (medium-high confidence)
        var pattern3 = new Regex(@"(?:No\.|Num|#)[:\s]*(?:[A-Za-z]+[ \t]+)*(\S+)", RegexOptions.IgnoreCase);
        var match3 = pattern3.Match(text);
        if (match3.Success)
        {
            var vale = match3.Groups[1].Value.ToUpperInvariant();
            if (vale.Any(char.IsLetterOrDigit))
            {
                _logger.LogDebug("Numero de vale matched with pattern 3 (No./Num/#): {Vale}", vale);
                return (vale, 0.85);
            }
        }

        _logger.LogDebug("No Numero de vale pattern matched in text");
        return (null, 0);
    }

    private bool TryParseDecimal(string value, out double result)
    {
        // Replace comma with period for cultures using comma as decimal separator
        var normalized = value.Replace(",", ".");
        return double.TryParse(normalized, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }
}
