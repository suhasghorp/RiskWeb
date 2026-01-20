using System.Text.Json;

namespace RiskWeb.Services.Chat;

public class GenericToolRegistry
{
    private readonly Dictionary<string, IMcpTool> _tools = new();
    private readonly ILogger<GenericToolRegistry> _logger;

    public GenericToolRegistry(
        GenericMongoQueryTool genericQueryTool,
        ExportResultsToExcelTool exportToExcel,
        ILogger<GenericToolRegistry> logger)
    {
        _logger = logger;

        RegisterTool(genericQueryTool);
        RegisterTool(exportToExcel);

        _logger.LogInformation("Generic Tool Registry initialized with {Count} tools", _tools.Count);
    }

    private void RegisterTool(IMcpTool tool)
    {
        _tools[tool.Name] = tool;
        _logger.LogInformation("Registered tool: {ToolName}", tool.Name);
    }

    public IMcpTool? GetTool(string name)
    {
        _tools.TryGetValue(name, out var tool);
        return tool;
    }

    public IEnumerable<IMcpTool> GetAllTools() => _tools.Values;

    public string GetToolsDescription()
    {
        var descriptions = _tools.Values.Select(t =>
            $"- {t.Name}: {t.Description}\n  Parameters: {t.ParametersSchema.GetRawText()}");
        return string.Join("\n\n", descriptions);
    }

    public JsonElement GetToolsSchemaForLlm()
    {
        var tools = _tools.Values.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.ParametersSchema
            }
        }).ToList();

        var json = JsonSerializer.Serialize(tools);
        return JsonDocument.Parse(json).RootElement;
    }
}
