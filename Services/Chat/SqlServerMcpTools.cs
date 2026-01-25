using System.Security.Claims;
using System.Text.Json;

namespace RiskWeb.Services.Chat;

/// <summary>
/// MCP Tool: List all tables in the SQL Server database
/// </summary>
public class ListTablesTool : IMcpTool
{
    private readonly ISqlServerMcpService _sqlService;
    private readonly ILogger<ListTablesTool> _logger;

    public string Name => "list_tables";
    public string Description => "List all tables available in the SQL Server database. Returns table names in schema.table format. Use this first to discover what data is available.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {},
            "required": []
        }
        """).RootElement;

    public ListTablesTool(ISqlServerMcpService sqlService, ILogger<ListTablesTool> logger)
    {
        _sqlService = sqlService;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            _logger.LogInformation("Executing list_tables");
            var tables = await _sqlService.GetTableNamesAsync();

            var result = new McpToolResult
            {
                Success = true,
                ResultType = "table_list",
                GeneratedQuery = "SELECT TABLE_SCHEMA + '.' + TABLE_NAME FROM INFORMATION_SCHEMA.TABLES",
                TotalCount = tables.Count
            };

            // Store tables in a format the LLM can understand
            result.SqlData = new SqlQueryData
            {
                Tables = tables,
                Message = $"Found {tables.Count} tables in the database."
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing list_tables");
            return McpToolResult.Error($"Error listing tables: {ex.Message}");
        }
    }
}

/// <summary>
/// MCP Tool: Get schema/structure of a specific table
/// </summary>
public class DescribeTableTool : IMcpTool
{
    private readonly ISqlServerMcpService _sqlService;
    private readonly ILogger<DescribeTableTool> _logger;

    public string Name => "describe_table";
    public string Description => "Get the schema and structure of a specific table. Returns column names, data types, nullability, and relationships. Use this to understand what columns are available before writing queries.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "table_name": {
                    "type": "string",
                    "description": "The name of the table to describe, in format 'schema.table' (e.g., 'dbo.Customers') or just 'table' (assumes dbo schema)"
                }
            },
            "required": ["table_name"]
        }
        """).RootElement;

    public DescribeTableTool(ISqlServerMcpService sqlService, ILogger<DescribeTableTool> logger)
    {
        _sqlService = sqlService;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var tableName = args.GetProperty("table_name").GetString();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return McpToolResult.Error("table_name parameter is required");
            }

            _logger.LogInformation("Executing describe_table for: {TableName}", tableName);
            var schema = await _sqlService.GetTableSchemaAsync(tableName);

            var result = new McpToolResult
            {
                Success = true,
                ResultType = "table_schema",
                GeneratedQuery = $"INFORMATION_SCHEMA query for {tableName}",
                TotalCount = schema.Columns.Count
            };

            result.SqlData = new SqlQueryData
            {
                TableSchema = schema,
                Message = $"Table {tableName} has {schema.Columns.Count} columns."
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing describe_table");
            return McpToolResult.Error($"Error describing table: {ex.Message}");
        }
    }
}

/// <summary>
/// MCP Tool: Execute a read-only SQL query
/// </summary>
public class ReadDataTool : IMcpTool
{
    private readonly ISqlServerMcpService _sqlService;
    private readonly ILogger<ReadDataTool> _logger;

    public string Name => "read_data";
    public string Description => "Execute a read-only SQL SELECT query against the database. Only SELECT queries are allowed. The query will automatically be limited to prevent returning too many rows. Use this to retrieve data after understanding the schema.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "query": {
                    "type": "string",
                    "description": "The SQL SELECT query to execute. Must be a valid SELECT statement. Do not include INSERT, UPDATE, DELETE, or other modifying statements."
                },
                "max_rows": {
                    "type": "integer",
                    "description": "Maximum number of rows to return (default: 100, max: 1000)",
                    "default": 100
                }
            },
            "required": ["query"]
        }
        """).RootElement;

    public ReadDataTool(ISqlServerMcpService sqlService, ILogger<ReadDataTool> logger)
    {
        _sqlService = sqlService;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var query = args.GetProperty("query").GetString();
            if (string.IsNullOrWhiteSpace(query))
            {
                return McpToolResult.Error("query parameter is required");
            }

            var maxRows = 100;
            if (args.TryGetProperty("max_rows", out var maxRowsElement))
            {
                maxRows = Math.Min(maxRowsElement.GetInt32(), 1000);
            }

            _logger.LogInformation("Executing read_data: {Query} (max_rows: {MaxRows})", query, maxRows);
            var queryResult = await _sqlService.ExecuteQueryAsync(query, maxRows);

            if (!queryResult.Success)
            {
                return McpToolResult.Error(queryResult.ErrorMessage ?? "Query execution failed");
            }

            var result = new McpToolResult
            {
                Success = true,
                ResultType = "query_results",
                GeneratedQuery = queryResult.ExecutedQuery ?? query,
                TotalCount = queryResult.TotalRowsReturned
            };

            result.SqlData = new SqlQueryData
            {
                Columns = queryResult.ColumnNames,
                Rows = queryResult.Rows,
                Message = $"Query returned {queryResult.TotalRowsReturned} rows."
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing read_data");
            return McpToolResult.Error($"Error executing query: {ex.Message}");
        }
    }
}

/// <summary>
/// MCP Tool: Get sample data from a table
/// </summary>
public class GetSampleDataTool : IMcpTool
{
    private readonly ISqlServerMcpService _sqlService;
    private readonly ILogger<GetSampleDataTool> _logger;

    public string Name => "get_sample_data";
    public string Description => "Get a few sample rows from a table to understand what kind of data it contains. Useful for understanding data formats, values, and relationships before writing complex queries.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "table_name": {
                    "type": "string",
                    "description": "The name of the table to sample, in format 'schema.table' (e.g., 'dbo.Orders') or just 'table'"
                },
                "sample_size": {
                    "type": "integer",
                    "description": "Number of sample rows to return (default: 5, max: 10)",
                    "default": 5
                }
            },
            "required": ["table_name"]
        }
        """).RootElement;

    public GetSampleDataTool(ISqlServerMcpService sqlService, ILogger<GetSampleDataTool> logger)
    {
        _sqlService = sqlService;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var tableName = args.GetProperty("table_name").GetString();
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return McpToolResult.Error("table_name parameter is required");
            }

            var sampleSize = 5;
            if (args.TryGetProperty("sample_size", out var sampleElement))
            {
                sampleSize = Math.Min(sampleElement.GetInt32(), 10);
            }

            _logger.LogInformation("Executing get_sample_data for: {TableName} (size: {SampleSize})", tableName, sampleSize);
            var sampleData = await _sqlService.GetSampleDataAsync(tableName, sampleSize);

            var result = new McpToolResult
            {
                Success = true,
                ResultType = "sample_data",
                GeneratedQuery = $"SELECT TOP {sampleSize} * FROM {tableName}",
                TotalCount = sampleData.Count
            };

            // Get column names from first row if available
            var columns = sampleData.FirstOrDefault()?.Keys.ToList() ?? new List<string>();

            result.SqlData = new SqlQueryData
            {
                Columns = columns,
                Rows = sampleData,
                Message = $"Retrieved {sampleData.Count} sample rows from {tableName}."
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing get_sample_data");
            return McpToolResult.Error($"Error getting sample data: {ex.Message}");
        }
    }
}

/// <summary>
/// MCP Tool: Export SQL query results to Excel
/// </summary>
public class ExportSqlToExcelTool : IMcpTool
{
    private readonly ISqlServerMcpService _sqlService;
    private readonly IExcelExportService _excelExport;
    private readonly ILogger<ExportSqlToExcelTool> _logger;

    public string Name => "export_to_excel";
    public string Description => "Export SQL query results to an Excel file. Use this when the user wants to download, export, or save query results to Excel/spreadsheet. Execute the query and export the results in one step.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "query": {
                    "type": "string",
                    "description": "The SQL SELECT query to execute and export. Must be a valid SELECT statement."
                },
                "max_rows": {
                    "type": "integer",
                    "description": "Maximum number of rows to export (default: 1000, max: 10000). Use 0 for all rows up to max limit.",
                    "default": 1000
                },
                "sheet_name": {
                    "type": "string",
                    "description": "Optional name for the Excel worksheet (default: 'Query Results')"
                }
            },
            "required": ["query"]
        }
        """).RootElement;

    public ExportSqlToExcelTool(
        ISqlServerMcpService sqlService,
        IExcelExportService excelExport,
        ILogger<ExportSqlToExcelTool> logger)
    {
        _sqlService = sqlService;
        _excelExport = excelExport;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var query = args.GetProperty("query").GetString();
            if (string.IsNullOrWhiteSpace(query))
            {
                return McpToolResult.Error("query parameter is required");
            }

            var maxRows = 1000;
            if (args.TryGetProperty("max_rows", out var maxRowsElement))
            {
                var requested = maxRowsElement.GetInt32();
                maxRows = requested <= 0 ? 10000 : Math.Min(requested, 10000);
            }

            string? sheetName = null;
            if (args.TryGetProperty("sheet_name", out var sheetElement))
            {
                sheetName = sheetElement.GetString();
            }

            _logger.LogInformation("Executing export_to_excel: {Query} (max_rows: {MaxRows})", query, maxRows);

            // Execute the query
            var queryResult = await _sqlService.ExecuteQueryAsync(query, maxRows);

            if (!queryResult.Success)
            {
                return McpToolResult.Error(queryResult.ErrorMessage ?? "Query execution failed");
            }

            if (queryResult.Rows.Count == 0)
            {
                return McpToolResult.Error("Query returned no results to export");
            }

            // Export to Excel
            var exportResult = await _excelExport.ExportSqlDataToExcelAsync(
                queryResult.ColumnNames,
                queryResult.Rows,
                queryResult.ExecutedQuery ?? query,
                sheetName);

            if (!exportResult.Success)
            {
                return McpToolResult.Error(exportResult.ErrorMessage ?? "Export failed");
            }

            _logger.LogInformation("SQL export successful: {FileName}, {RecordCount} records",
                exportResult.FileName, exportResult.RecordCount);

            var result = new McpToolResult
            {
                Success = true,
                ResultType = "export",
                GeneratedQuery = queryResult.ExecutedQuery ?? query,
                TotalCount = exportResult.RecordCount,
                ExportFileId = exportResult.FileId,
                ExportFileName = exportResult.FileName
            };

            result.SqlData = new SqlQueryData
            {
                Message = $"Exported {exportResult.RecordCount} rows to Excel file: {exportResult.FileName}"
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing export_to_excel");
            return McpToolResult.Error($"Export error: {ex.Message}");
        }
    }
}

/// <summary>
/// Registry for SQL Server MCP tools
/// </summary>
public class SqlServerMcpToolRegistry
{
    private readonly Dictionary<string, IMcpTool> _tools = new();
    private readonly ILogger<SqlServerMcpToolRegistry> _logger;

    public SqlServerMcpToolRegistry(
        ListTablesTool listTables,
        DescribeTableTool describeTable,
        ReadDataTool readData,
        GetSampleDataTool getSampleData,
        ExportSqlToExcelTool exportToExcel,
        ILogger<SqlServerMcpToolRegistry> logger)
    {
        _logger = logger;

        RegisterTool(listTables);
        RegisterTool(describeTable);
        RegisterTool(readData);
        RegisterTool(getSampleData);
        RegisterTool(exportToExcel);

        _logger.LogInformation("SQL Server MCP Tool Registry initialized with {Count} tools", _tools.Count);
    }

    private void RegisterTool(IMcpTool tool)
    {
        _tools[tool.Name] = tool;
        _logger.LogInformation("Registered SQL Server tool: {ToolName}", tool.Name);
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
}
