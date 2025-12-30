# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

.NET 10.0 Web API for OCR receipt processing using Azure Document Intelligence. Extracts structured data from receipt images (gas station receipts): merchant name, total amount, and license plate numbers (Placa).

## Commands

### Build & Run
```bash
# Restore dependencies
dotnet restore OCRTest/OCRTest.csproj

# Build the project
dotnet build OCRTest/OCRTest.csproj

# Run the API
dotnet run --project OCRTest/OCRTest.csproj

# Default URL: http://localhost:5093
```

### Testing Endpoints

Use the `OCRTest.http` file in Visual Studio/Rider/VS Code REST Client:
- `GET /api/health` - Health check & system status
- `GET /api/receipt` - List available receipt files
- `POST /api/receipt/process/{filename}` - Process single receipt
- `POST /api/receipt/process-all` - Batch process all receipts

Or with PowerShell:
```powershell
Invoke-WebRequest -Uri "http://localhost:5093/api/health" -Method GET
Invoke-WebRequest -Uri "http://localhost:5093/api/receipt/process/receipt1.jpg" -Method POST
```

### Verify Configuration
Check Azure credentials are set in `OCRTest/appsettings.json`:
```json
{
  "AzureDocumentIntelligence": {
    "Endpoint": "https://<your-resource>.cognitiveservices.azure.com/",
    "ApiKey": "<your-api-key>",
    "ModelId": "prebuilt-receipt"
  }
}
```

## Architecture

### Service Layer Pattern
```
Controllers (API endpoints)
    ↓
IReceiptProcessingService (business logic)
    ↓
IAzureDocumentIntelligenceService (Azure integration)
    ↓
Azure Document Intelligence API
```

**Key Files**:
- `Controllers/ReceiptController.cs` - REST endpoints
- `Controllers/HealthController.cs` - Health check endpoint
- `Services/ReceiptProcessingService.cs` - Receipt processing orchestration
- `Services/AzureDocumentIntelligenceService.cs` - Azure API client with rate limiting
- `Middleware/ExceptionHandlingMiddleware.cs` - Global error handling

### Dependency Injection (Program.cs)
Uses Options pattern for configuration:
```csharp
builder.Services.Configure<AzureDocumentIntelligenceOptions>(
    builder.Configuration.GetSection("AzureDocumentIntelligence"));

builder.Services.AddScoped<IAzureDocumentIntelligenceService, AzureDocumentIntelligenceService>();
builder.Services.AddScoped<IReceiptProcessingService, ReceiptProcessingService>();
```

### Azure Document Intelligence Integration

**Service**: Azure Form Recognizer (Document Intelligence)
- **Model**: `prebuilt-receipt` (built-in receipt recognition)
- **Tier**: F0 (free tier) - **1 request per 60 seconds**
- **SDK**: Azure.AI.FormRecognizer v4.1.0

**Rate Limiting** (AzureDocumentIntelligenceService.cs:13-14, 35-45):
- Uses `SemaphoreSlim` for thread-safe rate limiting
- Tracks `_lastRequestTime` globally
- Enforces 60-second delays between requests automatically
- Logs rate limiting events

**Error Handling**:
- 401: Authentication failure → returns 503 ServiceUnavailable
- 429: Rate limit exceeded → returns 429 TooManyRequests
- File not found → returns 404 NotFound

## Configuration

### appsettings.json Structure

**AzureDocumentIntelligence**:
- `Endpoint`: Azure resource endpoint URL
- `ApiKey`: Azure subscription key
- `ModelId`: "prebuilt-receipt" (don't change unless using custom model)

**ReceiptProcessing**:
- `ReceiptsFolderPath`: "exampleReceipts" (relative to project root)
- `SupportedExtensions`: [".jpg", ".jpeg", ".png", ".pdf"]
- `MaxFileSizeMB`: 50

## Key Implementation Details

### Custom Field Extraction: Placa (License Plate)

The system uses 3-tier regex pattern matching for license plate extraction (ReceiptProcessingService.cs:ExtractReceiptData):

1. **Pattern 1** (confidence 0.9): `Placa:\s*([A-Z0-9-]+)` or `Placa\s+([A-Z0-9-]+)`
2. **Pattern 2** (confidence 0.85): `PLACA\s+([A-Z0-9-]+)`
3. **Pattern 3** (confidence 0.6): Generic format `[A-Z]{3}-\d{3}`

Falls back to lower confidence patterns if higher confidence patterns don't match.

### Field Mapping (Spanish)
- `MerchantName` → `NombreDeLaGasolinera` (gas station name)
- `Total` → `Total` (handles Currency and Double field types)
- Custom OCR → `Placa` (license plate, extracted via regex)

### Error Handling (ExceptionHandlingMiddleware.cs)

Global middleware catches all exceptions and returns standardized JSON:
```json
{
  "success": false,
  "error": {
    "code": "ErrorCode",
    "message": "Error message"
  },
  "timestamp": "2025-12-23T12:00:00Z"
}
```

**Error codes**:
- `FileNotFound` - Receipt file doesn't exist
- `RateLimitExceeded` - Azure F0 tier quota exceeded
- `ProcessingError` - Azure API failures (auth, service errors)
- `InternalError` - Unexpected exceptions

### Confidence Scoring

OCR results include confidence scores (0-1) for each extracted field:
```json
{
  "confidence": {
    "nombreDeLaGasolinera": 0.95,
    "total": 0.98,
    "placa": 0.85
  }
}
```

Lower confidence scores may indicate unreliable extraction.

## Data Flow

1. **Request**: `POST /api/receipt/process/{filename}`
2. **ReceiptController**: Validates request, calls service
3. **ReceiptProcessingService**:
   - Validates file exists in `exampleReceipts/`
   - Opens file stream
   - Calls Azure service
4. **AzureDocumentIntelligenceService**:
   - Enforces rate limiting (60s delay if needed)
   - Sends document to Azure Document Intelligence
   - Returns `AnalyzeResult`
5. **ExtractReceiptData**:
   - Maps Azure fields to Spanish field names
   - Runs Placa regex extraction on OCR text
   - Calculates confidence scores
6. **Response**: Returns `ReceiptResponse` with data + metadata

## Receipt Files

Place receipt images in `OCRTest/exampleReceipts/` folder:
- Supported formats: JPG, JPEG, PNG, PDF
- Max file size: 50 MB
- Optimal quality: 300+ DPI for best OCR accuracy
