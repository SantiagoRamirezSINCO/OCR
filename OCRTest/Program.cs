using OCRTest.Middleware;
using OCRTest.Models;
using OCRTest.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Configure Azure Document Intelligence options
builder.Services.Configure<AzureDocumentIntelligenceOptions>(
    builder.Configuration.GetSection("AzureDocumentIntelligence"));

// Register application services
builder.Services.AddScoped<IAzureDocumentIntelligenceService, AzureDocumentIntelligenceService>();
builder.Services.AddScoped<IReceiptProcessingService, ReceiptProcessingService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Add exception handling middleware
app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseHttpsRedirection();

app.MapControllers();

app.Run();