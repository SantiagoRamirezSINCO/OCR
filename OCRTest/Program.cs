using Microsoft.EntityFrameworkCore;
using OCRTest.Data;
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

// Configure Entity Framework Core with PostgreSQL
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddDbContext<ReceiptDbContext>(options =>
    {
        options.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: null);
        });

        if (builder.Environment.IsDevelopment())
        {
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
        }
    });

    // Register database-backed services
    builder.Services.AddScoped<IReceiptRepository, ReceiptRepository>();
    builder.Services.AddScoped<IReceiptStorageService, ReceiptStorageService>();
    builder.Services.AddScoped<IFileValidationService, FileValidationService>();
}

// Register application services
builder.Services.AddScoped<IAzureDocumentIntelligenceService, AzureDocumentIntelligenceService>();
builder.Services.AddScoped<IReceiptProcessingService, ReceiptProcessingService>();

var app = builder.Build();

// Run database migrations on startup (Production/Staging only)
if ((app.Environment.IsProduction() || app.Environment.IsStaging()) && !string.IsNullOrEmpty(connectionString))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetService<ReceiptDbContext>();
    if (dbContext != null)
    {
        try
        {
            await dbContext.Database.MigrateAsync();
            app.Logger.LogInformation("Database migrations applied successfully");
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Error applying database migrations");
            // Don't throw - allow app to start even if migrations fail
        }
    }
}

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