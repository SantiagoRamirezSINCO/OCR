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

    public ReceiptProcessingService(
        IAzureDocumentIntelligenceService azureService,
        ILogger<ReceiptProcessingService> logger)
    {
        _azureService = azureService;
        _logger = logger;
    }

    public async Task<ReceiptResponse> ProcessReceiptAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = new ReceiptResponse { FileName = fileName };

        try
        {
            _logger.LogInformation("Processing uploaded receipt: {FileName}", fileName);

            var analyzeResult = await _azureService.AnalyzeReceiptAsync(fileStream, cancellationToken);

            var (data, confidence) = ExtractReceiptData(analyzeResult);

            response.Success = true;
            response.Data = data;
            response.Confidence = confidence;
            response.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

            _logger.LogInformation("Successfully processed uploaded receipt: {FileName} in {Ms}ms", fileName, stopwatch.ElapsedMilliseconds);
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
            _logger.LogError(ex, "Error processing uploaded receipt {FileName}", fileName);
            response.Success = false;
            response.Error = new ProcessingError
            {
                Code = "ProcessingError",
                Message = ex.Message
            };
        }

        return response;
    }

    private (ReceiptData data, ConfidenceScores confidence) ExtractReceiptData(AnalyzeResult result)
    {
        var data = new ReceiptData();
        var confidence = new ConfidenceScores();

        if (result.Documents?.Count > 0)
        {
            var document = result.Documents[0];

            if (document.Fields.TryGetValue("MerchantName", out var merchantField) && merchantField != null)
            {
                data.NombreDeLaGasolinera = merchantField.Content;
                confidence.NombreDeLaGasolinera = merchantField.Confidence ?? 0;
                _logger.LogDebug("Extracted MerchantName: {Value} (confidence: {Confidence})",
                    data.NombreDeLaGasolinera, confidence.NombreDeLaGasolinera);
            }

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

        // Extract all OCR text
        var allText = string.Join(" ", result.Pages
            .SelectMany(p => p.Lines ?? Enumerable.Empty<DocumentLine>())
            .Select(l => l.Content));

        _logger.LogDebug("Full OCR text: {Text}", allText);

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

        // Fallback: Fecha
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

        // Extract Cantidad
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

        // Extract Número de recibo
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

        // Extract NIT
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

        // "Placa: ABC123" or "Placa ABC-123" or "Placa HGW - 523"
        var pattern1 = new Regex(@"Placa[:\s]+([A-Z]{3}\s*-?\s*\d{3,4})", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success)
        {
            var placa = NormalizePlaca(match1.Groups[1].Value);
            _logger.LogDebug("Placa matched with pattern 1 (Placa:): {Placa}", placa);
            return (placa, 0.9);
        }

        // "PLACA ABC123" or "PLACA HGW - 523" 
        var pattern2 = new Regex(@"PLACA\s+([A-Z]{3}\s*-?\s*\d{3,4})", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success)
        {
            var placa = NormalizePlaca(match2.Groups[1].Value);
            _logger.LogDebug("Placa matched with pattern 2 (PLACA): {Placa}", placa);
            return (placa, 0.85);
        }

        // ABC123 or ABC-123 or HGW - 523
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
        var normalized = placa.Trim().ToUpperInvariant();

        normalized = normalized.Replace(" ", "");

        if (!normalized.Contains("-") && normalized.Length >= 6)
        {
            normalized = normalized.Insert(3, "-");
        }

        _logger.LogDebug("Normalized Placa from '{Original}' to '{Normalized}'", placa, normalized);
        return normalized;
    }

    private (DateTime? fecha, double confidence) ExtractFechaDeTanqueo(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // "Fecha: 2024-12-15" or "Fecha 2024-12-15"
        var pattern1 = new Regex(@"Fecha[:\s]+(20\d{2}[-/]\d{2}[-/]\d{2})", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success && DateTime.TryParse(match1.Groups[1].Value, out var fecha1))
        {
            _logger.LogDebug("Fecha matched with pattern 1 (Fecha: ISO): {Fecha}", fecha1);
            return (fecha1, 0.9);
        }

        // format 2024-12-15
        var pattern2 = new Regex(@"\b(20\d{2}[-/]\d{2}[-/]\d{2})\b");
        var match2 = pattern2.Match(text);
        if (match2.Success && DateTime.TryParse(match2.Groups[1].Value, out var fecha2))
        {
            _logger.LogDebug("Fecha matched with pattern 2 (ISO): {Fecha}", fecha2);
            return (fecha2, 0.85);
        }

        // DD/MM/YYYY or DD-MM-YYYY 
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

        // "Cantidad: 15.5 Gal" or "Volumen: 5.334"
        var pattern1 = new Regex(@"(?:Cantidad|Volumen|Galones)[:\s]+([\d,.]+)\s*(?:Gal|Galones)?", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success && TryParseDecimal(match1.Groups[1].Value, out var cantidad1))
        {
            _logger.LogDebug("Cantidad matched with pattern 1 (Cantidad/Volumen:): {Cantidad}", cantidad1);
            return (cantidad1, 0.9);
        }

        // "GAL :G 10.366" 
        var pattern2 = new Regex(@"GAL\s*:G\s*([\d,.]+)", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success && TryParseDecimal(match2.Groups[1].Value, out var cantidad2))
        {
            _logger.LogDebug("Cantidad matched with pattern 2 (GAL :G): {Cantidad}", cantidad2);
            return (cantidad2, 0.88);
        }
        
        // "Corriente: 15.5 Gal"
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

        // "Kilometraje: 125000" or "Kilometros: 23453" or "Odómetro: 125000"
        var pattern1 = new Regex(@"(?:Kilometraje|Kilometros|Odometro|Odómetro)[:\s]+(\d{1,7})(?:\s*KM)?", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success && int.TryParse(match1.Groups[1].Value, out var km1))
        {
            _logger.LogDebug("Kilometraje matched with pattern 1 (Kilometraje/Kilometros/Odómetro:): {Km}", km1);
            return (km1, 0.9);
        }

        // "KM: 125000" or "KM 125000"
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

        // "ORDEN DE VENTA: 4894" or "ORDEN DE PEDIDO: 123" 
        var pattern1 = new Regex(@"ORDEN\s+DE\s+(?:VENTA|PEDIDO)[:\s]+(?:[A-Za-z]+[ \t]+)*(\S+)", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success)
        {
            var vale = match1.Groups[1].Value.ToUpperInvariant();
            if (vale.Any(char.IsLetterOrDigit))
            {
                _logger.LogDebug("Numero de vale matched with pattern 1 (ORDEN DE VENTA/PEDIDO:): {Vale}", vale);
                return (vale, 0.95);
            }
        }

        // "Numero: OP1-51928" or "Vale: ABC123" 
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

        // "No. RESH 81651653" 
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

        // "NIT: 900.291.461-4" or "NIT.900.291.461-4" 
        var pattern1 = new Regex(@"NIT[:\s.]+(\d{1,3}(?:[.,]\d{3})*-\d)", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success)
        {
            var nit = NormalizeNIT(match1.Groups[1].Value);
            _logger.LogDebug("NIT matched with pattern 1 (NIT: labeled): {NIT}", nit);
            return (nit, 0.92);
        }

        // "NIT900.291.461-4"
        var pattern2 = new Regex(@"NIT[:\s.]*(\d[\d.,]+\d-\d)", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success)
        {
            var nit = NormalizeNIT(match2.Groups[1].Value);
            _logger.LogDebug("NIT matched with pattern 2 (NIT relaxed): {NIT}", nit);
            return (nit, 0.85);
        }

        // "900.291.461-4"
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
        var normalized = nit.Replace(",", ".");
        normalized = normalized.Trim();

        _logger.LogDebug("Normalized NIT from '{Original}' to '{Normalized}'", nit, normalized);
        return normalized;
    }

    private (string? tipoCombustible, double confidence) ExtractTipoDeCombustible(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, 0);

        // "Combustible: Corriente"
        var pattern1 = new Regex(@"Combustible[:\s]+(Corriente|ACPM|Urea)", RegexOptions.IgnoreCase);
        var match1 = pattern1.Match(text);
        if (match1.Success)
        {
            var tipo = NormalizeTipoCombustible(match1.Groups[1].Value);
            _logger.LogDebug("Tipo de combustible matched with pattern 1 (Combustible:): {Tipo}", tipo);
            return (tipo, 0.95);
        }

        // "Tipo: ACPM" or "Producto: Corriente"
        var pattern2 = new Regex(@"(?:Tipo|Producto)[:\s]+(Corriente|ACPM|Urea)", RegexOptions.IgnoreCase);
        var match2 = pattern2.Match(text);
        if (match2.Success)
        {
            var tipo = NormalizeTipoCombustible(match2.Groups[1].Value);
            _logger.LogDebug("Tipo de combustible matched with pattern 2 (Tipo/Producto:): {Tipo}", tipo);
            return (tipo, 0.88);
        }

        // "Corriente 15.5 Gal"
        var pattern3 = new Regex(@"(Corriente|ACPM|Urea)[\s:]+\d+[.,]?\d*\s*(?:Gal|Galones|Lts|Litros)", RegexOptions.IgnoreCase);
        var match3 = pattern3.Match(text);
        if (match3.Success)
        {
            var tipo = NormalizeTipoCombustible(match3.Groups[1].Value);
            _logger.LogDebug("Tipo de combustible matched with pattern 3 (near quantity): {Tipo}", tipo);
            return (tipo, 0.78);
        }

        // Corriente or ACPM or Urea
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
        var normalized = tipo.Trim().ToUpperInvariant();

        return normalized switch
        {
            "CORRIENTE" => "Corriente",
            "ACPM" => "ACPM", 
            "UREA" => "Urea",
            _ => tipo
        };
    }

    private bool TryParseDecimal(string value, out double result)
    {
        var normalized = value.Replace(",", ".");
        return double.TryParse(normalized, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }
}
