using System.Security.Claims;
using System.Text.Json;
using MongoDB.Bson;
using RiskWeb.Models;

namespace RiskWeb.Services.Chat;

public interface IMcpTool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }
    Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user);
}

public class McpToolResult
{
    public bool Success { get; set; }
    public string? GeneratedQuery { get; set; }
    public List<BsonDocument> Documents { get; set; } = new();
    public List<Movie> Movies { get; set; } = new();
    public List<YearCount> YearCounts { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public int TotalCount { get; set; }
    public string ResultType { get; set; } = "documents"; // "documents", "movies", "yearcounts", "export", "table_list", "table_schema", "query_results", "sample_data"

    // Export-specific properties
    public string? ExportFileId { get; set; }
    public string? ExportFileName { get; set; }

    // SQL Server MCP properties
    public SqlQueryData? SqlData { get; set; }

    public static McpToolResult Ok(List<BsonDocument> documents, string query)
    {
        return new McpToolResult
        {
            Success = true,
            Documents = documents,
            GeneratedQuery = query,
            TotalCount = documents.Count,
            ResultType = "documents"
        };
    }

    public static McpToolResult FromMovies(List<Movie> movies, string query)
    {
        return new McpToolResult
        {
            Success = true,
            Movies = movies,
            GeneratedQuery = query,
            TotalCount = movies.Count,
            ResultType = "movies"
        };
    }

    public static McpToolResult FromYearCounts(List<YearCount> yearCounts, string query)
    {
        return new McpToolResult
        {
            Success = true,
            YearCounts = yearCounts,
            GeneratedQuery = query,
            TotalCount = yearCounts.Count,
            ResultType = "yearcounts"
        };
    }

    public static McpToolResult FromExport(string fileId, string fileName, int recordCount, string description)
    {
        return new McpToolResult
        {
            Success = true,
            ExportFileId = fileId,
            ExportFileName = fileName,
            TotalCount = recordCount,
            GeneratedQuery = description,
            ResultType = "export"
        };
    }

    public static McpToolResult Error(string message)
    {
        return new McpToolResult
        {
            Success = false,
            ErrorMessage = message
        };
    }
}

public class ToolCall
{
    public string ToolCallId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public JsonElement Arguments { get; set; }
    public McpToolResult? Result { get; set; }
}

public class ChatMessage
{
    public string Role { get; set; } = string.Empty; // "user", "assistant", "system"
    public string Content { get; set; } = string.Empty;
    public List<ToolCall>? ToolCalls { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ChatSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public List<ChatMessage> Messages { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    public void AddMessage(string role, string content, List<ToolCall>? toolCalls = null)
    {
        Messages.Add(new ChatMessage
        {
            Role = role,
            Content = content,
            ToolCalls = toolCalls,
            Timestamp = DateTime.UtcNow
        });
        LastActivityAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Data structure for SQL Server MCP tool results
/// </summary>
public class SqlQueryData
{
    public string? Message { get; set; }
    public List<string>? Tables { get; set; }
    public TableSchema? TableSchema { get; set; }
    public List<string>? Columns { get; set; }
    public List<Dictionary<string, object?>>? Rows { get; set; }
}
