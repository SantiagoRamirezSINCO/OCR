using System.Diagnostics;
using System.Text.Json;
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
    private readonly IReceiptRepository? _repository;
    private readonly IReceiptStorageService? _storageService;
    private readonly IFileValidationService? _validationService;
    private readonly string _receiptsFolderPath;
    private readonly string[] _supportedExtensions;

    public ReceiptProcessingService(
        IAzureDocumentIntelligenceService azureService,
        ILogger<ReceiptProcessingService> logger,
        IConfiguration configuration,
        IReceiptRepository? repository = null,
        IReceiptStorageService? storageService = null,
        IFileValidationService? validationService = null)
    {
        _azureService = azureService;
        _logger = logger;
        _repository = repository;
        _storageService = storageService;
        _validationService = validationService;
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
                Message = "Azure rate limit exceeded. Please wait before retrying."
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

        // Extract NIT (Número de Identificación Tributaria)
        var (nit, nitConfidence) = ExtractNIT(allText);
        data.NIT = nit;
        confidence.NIT = nitConfidence;

        if (!string.IsNullOrEmpty(nit))
        {
            _logger.LogDebug("Extracted NIT: {Value} (confidence: {Confidence})", nit, nitConfidence);
        }
        else
        {
            _logger.LogWarning("Could not extract NIT from receipt");
        }

        // Extract Tipo de Combustible
        var (tipoCombustible, tipoConfidence) = ExtractTipoDeCombustible(allText);
        data.TipoDeCombustible = tipoCombustible;
        confidence.TipoDeCombustible = tipoConfidence;

        if (!string.IsNullOrEmpty(tipoCombustible))
        {
            _logger.LogDebug("Extracted Tipo de combustible: {Value} (confidence: {Confidence})",
                tipoCombustible, tipoConfidence);
        }
        else
        {
            _logger.LogWarning("Could not extract Tipo de combustible from receipt");
        }

        return (data, confidence);
    }

    private (string? placa, double confidence) ExtractPlaca(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // Pattern 1: "Placa: ABC123" or "Placa ABC-123" or "Placa HGW - 523" (highest confidence)
        var pattern1 = new Regex(@"Placa[:\s]+([A-Z]{3}\s*-?\s*\d{3,4})", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success)
        {
            var placa = NormalizePlaca(match1.Groups[1].Value);
            _logger.LogDebug("Placa matched with pattern 1 (Placa:): {Placa}", placa);
            return (placa, 0.9);
        }

        // Pattern 2: "PLACA ABC123" or "PLACA HGW - 523" (high confidence)
        var pattern2 = new Regex(@"PLACA\s+([A-Z]{3}\s*-?\s*\d{3,4})", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success)
        {
            var placa = NormalizePlaca(match2.Groups[1].Value);
            _logger.LogDebug("Placa matched with pattern 2 (PLACA): {Placa}", placa);
            return (placa, 0.85);
        }

        // Pattern 3: Generic license plate format ABC123 or ABC-123 or HGW - 523 (lower confidence)
        var pattern3 = new Regex(@"\b([A-Z]{3}\s*-?\s*\d{3,4})\b");
        var match3 = pattern3.Match(text);
        if (match3.Success)
        {
            var placa = NormalizePlaca(match3.Groups[1].Value);
            _logger.LogDebug("Placa matched with pattern 3 (generic): {Placa}", placa);
            return (placa, 0.6);
        }

        _logger.LogDebug("No Placa pattern matched in text");
        return (null, 0);
    }

    private string NormalizePlaca(string placa)
    {
        // Remove extra spaces and standardize format to ABC-123
        var normalized = placa.Trim().ToUpperInvariant();

        // Remove all spaces
        normalized = normalized.Replace(" ", "");

        // Ensure format ABC-123 (add hyphen if missing)
        if (!normalized.Contains("-") && normalized.Length >= 6)
        {
            // Insert hyphen between letters and numbers (ABC123 -> ABC-123)
            normalized = normalized.Insert(3, "-");
        }

        _logger.LogDebug("Normalized Placa from '{Original}' to '{Normalized}'", placa, normalized);
        return normalized;
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

    private (string? nit, double confidence) ExtractNIT(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // Pattern 1: "NIT: 900.291.461-4" o "NIT.900.291.461-4" (highest confidence)
        var pattern1 = new Regex(@"NIT[:\s.]+(\d{1,3}(?:[.,]\d{3})*-\d)", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success)
        {
            var nit = NormalizeNIT(match1.Groups[1].Value);
            _logger.LogDebug("NIT matched with pattern 1 (NIT: labeled): {NIT}", nit);
            return (nit, 0.92);
        }

        // Pattern 2: "NIT900.291.461-4" - relaxed spacing (high confidence)
        var pattern2 = new Regex(@"NIT[:\s.]*(\d[\d.,]+\d-\d)", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success)
        {
            var nit = NormalizeNIT(match2.Groups[1].Value);
            _logger.LogDebug("NIT matched with pattern 2 (NIT relaxed): {NIT}", nit);
            return (nit, 0.85);
        }

        // Pattern 3: Generic Colombian NIT format "900.291.461-4" (medium confidence)
        var pattern3 = new Regex(@"\b(\d{3}[.,]\d{3}[.,]\d{3}-\d)\b");
        var match3 = pattern3.Match(text);
        if (match3.Success)
        {
            var nit = NormalizeNIT(match3.Groups[1].Value);
            _logger.LogDebug("NIT matched with pattern 3 (generic format): {NIT}", nit);
            return (nit, 0.70);
        }

        _logger.LogDebug("No NIT pattern matched in text");
        return (null, 0);
    }

    private string NormalizeNIT(string nit)
    {
        // Ensure periods are used as separators (not commas)
        // Format: XXX.XXX.XXX-X
        var normalized = nit.Replace(",", ".");
        normalized = normalized.Trim();

        _logger.LogDebug("Normalized NIT from '{Original}' to '{Normalized}'", nit, normalized);
        return normalized;
    }

    private (string? tipoCombustible, double confidence) ExtractTipoDeCombustible(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // Pattern 1: "Combustible: Corriente" (highest confidence)
        var pattern1 = new Regex(@"Combustible[:\s]+(Corriente|ACPM|Urea)", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success)
        {
            var tipo = NormalizeTipoCombustible(match1.Groups[1].Value);
            _logger.LogDebug("Tipo de combustible matched with pattern 1 (Combustible:): {Tipo}", tipo);
            return (tipo, 0.95);
        }

        // Pattern 2: "Tipo: ACPM" or "Producto: Corriente" (high confidence)
        var pattern2 = new Regex(@"(?:Tipo|Producto)[:\s]+(Corriente|ACPM|Urea)", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success)
        {
            var tipo = NormalizeTipoCombustible(match2.Groups[1].Value);
            _logger.LogDebug("Tipo de combustible matched with pattern 2 (Tipo/Producto:): {Tipo}", tipo);
            return (tipo, 0.88);
        }

        // Pattern 3: Near quantity "Corriente 15.5 Gal" (medium confidence)
        var pattern3 = new Regex(@"(Corriente|ACPM|Urea)[\s:]+\d+[.,]?\d*\s*(?:Gal|Galones|Lts|Litros)", RegexOptions.IgnoreCase);
        var match3 = pattern3.Match(text);
        if (match3.Success)
        {
            var tipo = NormalizeTipoCombustible(match3.Groups[1].Value);
            _logger.LogDebug("Tipo de combustible matched with pattern 3 (near quantity): {Tipo}", tipo);
            return (tipo, 0.78);
        }

        // Pattern 4: Standalone fuel type word (lower confidence)
        var pattern4 = new Regex(@"\b(Corriente|ACPM|Urea)\b", RegexOptions.IgnoreCase);
        var match4 = pattern4.Match(text);
        if (match4.Success)
        {
            var tipo = NormalizeTipoCombustible(match4.Groups[1].Value);
            _logger.LogDebug("Tipo de combustible matched with pattern 4 (standalone): {Tipo}", tipo);
            return (tipo, 0.65);
        }

        _logger.LogDebug("No Tipo de combustible pattern matched in text");
        return (null, 0);
    }

    private string NormalizeTipoCombustible(string tipo)
    {
        // Standardize casing: "Corriente", "ACPM", "Urea"
        var normalized = tipo.Trim().ToUpperInvariant();

        return normalized switch
        {
            "CORRIENTE" => "Corriente",
            "ACPM" => "ACPM", // Acronym stays uppercase
            "UREA" => "Urea",
            _ => tipo // Fallback to original if unexpected value
        };
    }

    private bool TryParseDecimal(string value, out double result)
    {
        // Replace comma with period for cultures using comma as decimal separator
        var normalized = value.Replace(",", ".");
        return double.TryParse(normalized, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    // ============= New Database-Backed Methods =============

    public async Task<ReceiptUploadResponse> ProcessUploadedReceiptAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (_repository == null || _storageService == null || _validationService == null)
        {
            throw new InvalidOperationException("Database services are not configured. Ensure IReceiptRepository, IReceiptStorageService, and IFileValidationService are registered.");
        }

        var stopwatch = Stopwatch.StartNew();
        var receiptId = Guid.NewGuid();

        // Step 1: Validate file
        var validationResult = await _validationService.ValidateUploadAsync(file);
        if (!validationResult.IsValid)
        {
            _logger.LogWarning("File validation failed for {FileName}: {Errors}",
                file.FileName, string.Join(", ", validationResult.Errors));

            throw new InvalidOperationException($"File validation failed: {string.Join(", ", validationResult.Errors)}");
        }

        // Step 2: Create receipt entity
        var receipt = new ReceiptEntity
        {
            Id = receiptId,
            FileName = file.FileName,
            FileSizeBytes = file.Length,
            MimeType = validationResult.MimeType ?? file.ContentType,
            PhotoPath = string.Empty, // Will be updated after saving
            ProcessingStatus = ProcessingStatus.Processing,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            // Step 3: Save to database (initial state)
            await _repository.CreateAsync(receipt, cancellationToken);
            _logger.LogInformation("Created receipt entity {ReceiptId} for file {FileName}", receiptId, file.FileName);

            // Step 4: Save photo to filesystem
            var photoPath = await _storageService.SavePhotoAsync(file, receiptId, cancellationToken);
            receipt.PhotoPath = photoPath;
            await _repository.UpdateAsync(receipt, cancellationToken);

            // Step 5: Process OCR
            using var stream = file.OpenReadStream();
            var analyzeResult = await _azureService.AnalyzeReceiptAsync(stream, cancellationToken);

            // Step 6: Extract data and confidence
            var (data, confidence) = ExtractReceiptData(analyzeResult);

            // Step 7: Extract raw OCR text
            var rawOcrText = string.Join(" ", analyzeResult.Pages
                .SelectMany(p => p.Lines ?? Enumerable.Empty<DocumentLine>())
                .Select(l => l.Content));

            // Step 8: Serialize to JSON
            var extractedFieldsJson = JsonSerializer.Serialize(data);
            var confidenceScoresJson = JsonSerializer.Serialize(confidence);

            // Step 9: Update receipt entity with results
            receipt.RawOcrText = rawOcrText;
            receipt.ExtractedFieldsJson = extractedFieldsJson;
            receipt.ConfidenceScoresJson = confidenceScoresJson;
            receipt.ProcessingStatus = ProcessingStatus.Completed;
            receipt.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;
            receipt.ProcessedAt = DateTime.UtcNow;

            await _repository.UpdateAsync(receipt, cancellationToken);

            _logger.LogInformation("Successfully processed uploaded receipt {ReceiptId} in {Ms}ms",
                receiptId, stopwatch.ElapsedMilliseconds);

            // Step 10: Return response
            return new ReceiptUploadResponse
            {
                Success = true,
                FileName = file.FileName,
                ReceiptId = receiptId,
                Data = data,
                Confidence = confidence,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 429)
        {
            _logger.LogWarning("Rate limit exceeded while processing uploaded receipt {ReceiptId}", receiptId);

            receipt.ProcessingStatus = ProcessingStatus.Failed;
            receipt.ErrorMessage = "Azure rate limit exceeded";
            await _repository.UpdateAsync(receipt, cancellationToken);

            throw new InvalidOperationException("Azure rate limit exceeded. Please wait before retrying.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing uploaded receipt {ReceiptId}", receiptId);

            receipt.ProcessingStatus = ProcessingStatus.Failed;
            receipt.ErrorMessage = ex.Message;
            await _repository.UpdateAsync(receipt, cancellationToken);

            throw;
        }
    }

    public async Task<ReceiptEntity?> GetReceiptByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (_repository == null)
        {
            throw new InvalidOperationException("Database services are not configured. Ensure IReceiptRepository is registered.");
        }

        return await _repository.GetByIdAsync(id, cancellationToken);
    }

    public async Task<PagedReceiptResponse> GetReceiptsPagedAsync(
        int page,
        int pageSize,
        ProcessingStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        if (_repository == null)
        {
            throw new InvalidOperationException("Database services are not configured. Ensure IReceiptRepository is registered.");
        }

        var (items, totalCount) = await _repository.GetPagedAsync(page, pageSize, status, cancellationToken);

        var receipts = items.Select(r => new ReceiptSummary
        {
            Id = r.Id,
            FileName = r.FileName,
            ProcessingStatus = r.ProcessingStatus.ToString(),
            CreatedAt = r.CreatedAt,
            ProcessedAt = r.ProcessedAt
        }).ToList();

        return new PagedReceiptResponse
        {
            Receipts = receipts,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }
}
