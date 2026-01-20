using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using RiskWeb.Components;
using RiskWeb.Data;
using RiskWeb.Services;
using RiskWeb.Services.Chat;
using Serilog;
using Serilog.Events;

// Configure Serilog for real-time file logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(AppContext.BaseDirectory, "logs", "riskweb-.log"),
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        flushToDiskInterval: TimeSpan.FromSeconds(1))  // Flush every second for real-time logging
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Use Serilog for logging
builder.Host.UseSerilog();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configure Windows Authentication
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

builder.Services.AddAuthorization();

// Add DbContext
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));


builder.Services.AddScoped<ModuleStateService>();
builder.Services.AddScoped<IUserRoleService, UserRoleService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMudServices();

// Chat services
builder.Services.AddSingleton<IMongoDbService, MongoDbService>();
builder.Services.AddSingleton<IChatSessionService, ChatSessionService>();

// Register LLM client based on configuration
var llmProvider = builder.Configuration["LlmProvider"] ?? "OpenRouter";
Log.Information("Using LLM Provider: {Provider}", llmProvider);
if (llmProvider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ILlmClient, AzureOpenAiClient>();
}
else
{
    builder.Services.AddSingleton<ILlmClient, OpenRouterClient>();
}

builder.Services.AddSingleton<IExcelExportService, ExcelExportService>();
builder.Services.AddSingleton<FindMoviesByGenreTool>();
builder.Services.AddSingleton<FindMoviesByYearTool>();
builder.Services.AddSingleton<FindMoviesByGenreAndYearTool>();
builder.Services.AddSingleton<CountMoviesPerYearTool>();
builder.Services.AddSingleton<ExportResultsToExcelTool>();
builder.Services.AddSingleton<McpToolRegistry>();
builder.Services.AddScoped<IQueryOrchestrator, QueryOrchestrator>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

// Excel export download endpoint
app.MapGet("/api/export/{fileId}", (string fileId, IExcelExportService exportService) =>
{
    var fileData = exportService.GetExportedFile(fileId);
    if (fileData == null)
        return Results.NotFound("Export file not found or expired");

    return Results.File(fileData, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"export_{fileId}.xlsx");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
