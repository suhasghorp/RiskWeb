using System.Security.Claims;
using System.Text.Json;
using MongoDB.Bson;

namespace RiskWeb.Services.Chat;

public interface IQueryOrchestrator
{
    Task<OrchestratorResponse> ProcessQuestionAsync(string question, ChatSession session, ClaimsPrincipal? user);
}

public class OrchestratorResponse
{
    public bool Success { get; set; }
    public string Answer { get; set; } = string.Empty;
    public List<ToolCall> ToolCalls { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public class QueryOrchestrator : IQueryOrchestrator
{
    private readonly ILlmClient _llmClient;
    private readonly McpToolRegistry _toolRegistry;
    private readonly ILogger<QueryOrchestrator> _logger;

    private const string SystemPrompt = """
        You are a helpful assistant that helps users query a MongoDB movie database.
        You have access to tools that can search for movies by genre, year, or both, count movies per year, and export results to Excel.

        Available collections:
        - movies: Contains movie documents with fields like title, year, genres, cast, directors, plot, runtime, rated, imdb (rating, votes), etc.

        IMPORTANT - Choose the right search tool:
        - find_movies_by_genre: Use when filtering by genre ONLY
        - find_movies_by_year: Use when filtering by year ONLY
        - find_movies_by_genre_and_year: Use when filtering by BOTH genre AND year (e.g., "action movies from 2020", "comedy films in 1995")
        - count_movies_per_year: Use for counting movies per year

        When user says "all" or "everything", set limit to 0 to get all matching records.

        IMPORTANT - For export requests (export, download, save to Excel/spreadsheet):
        - Call export_to_excel DIRECTLY - do NOT run a search query first
        - Choose the correct export_type:
          * "movies_by_genre" - when filtering by genre only
          * "movies_by_year" - when filtering by year only
          * "movies_by_genre_and_year" - when filtering by BOTH genre AND year
          * "movies_by_year_range" - when filtering by a range of years
          * "movie_counts" - for movie count statistics
        - When user says "all", set limit to 0 to export all matching records

        Always use tools to fetch data - never make up movie information.
        After receiving tool results, summarize the findings in a helpful way.
        If no results are found, suggest alternative search criteria.
        Keep responses concise and focused on the query results.
        """;

    public QueryOrchestrator(
        ILlmClient llmClient,
        McpToolRegistry toolRegistry,
        ILogger<QueryOrchestrator> logger)
    {
        _llmClient = llmClient;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public async Task<OrchestratorResponse> ProcessQuestionAsync(
        string question,
        ChatSession session,
        ClaimsPrincipal? user)
    {
        _logger.LogInformation("Processing question: {Question}", question);

        var toolCalls = new List<ToolCall>();

        try
        {
            // Build conversation messages
            var messages = BuildConversationMessages(session, question);

            // Build tools list for LLM
            var tools = BuildToolsList();

            // First LLM call to understand the question and potentially call tools
            var request = new LlmRequest
            {
                Messages = messages,
                Tools = tools,
                Temperature = 0.3
            };

            var response = await _llmClient.ChatAsync(request);

            if (!response.Success)
            {
                return new OrchestratorResponse
                {
                    Success = false,
                    ErrorMessage = response.ErrorMessage
                };
            }

            // Check if LLM wants to call tools
            if (response.ToolCalls != null && response.ToolCalls.Count > 0)
            {
                _logger.LogInformation("LLM requested {Count} tool calls", response.ToolCalls.Count);

                // Execute tool calls
                var toolResults = await ExecuteToolCallsAsync(response.ToolCalls, user);
                toolCalls.AddRange(toolResults);

                // Add assistant message with tool calls
                messages.Add(new LlmMessage
                {
                    Role = "assistant",
                    Content = response.Content,
                    ToolCalls = response.ToolCalls
                });

                // Add tool results as messages - use the preserved ToolCallId
                foreach (var toolResult in toolResults)
                {
                    var resultContent = FormatToolResult(toolResult);
                    messages.Add(new LlmMessage
                    {
                        Role = "tool",
                        Content = resultContent,
                        ToolCallId = toolResult.ToolCallId  // Use the stored ID, not lookup by name
                    });
                }

                // Second LLM call to summarize results
                var summaryRequest = new LlmRequest
                {
                    Messages = messages,
                    Temperature = 0.5
                };

                var summaryResponse = await _llmClient.ChatAsync(summaryRequest);

                if (!summaryResponse.Success)
                {
                    return new OrchestratorResponse
                    {
                        Success = false,
                        ErrorMessage = summaryResponse.ErrorMessage,
                        ToolCalls = toolCalls
                    };
                }

                return new OrchestratorResponse
                {
                    Success = true,
                    Answer = summaryResponse.Content ?? "No response generated.",
                    ToolCalls = toolCalls
                };
            }

            // No tool calls - return direct response
            return new OrchestratorResponse
            {
                Success = true,
                Answer = response.Content ?? "I'm not sure how to help with that. Try asking about movies by genre or year.",
                ToolCalls = toolCalls
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing question");
            return new OrchestratorResponse
            {
                Success = false,
                ErrorMessage = $"Error: {ex.Message}",
                ToolCalls = toolCalls
            };
        }
    }

    private List<LlmMessage> BuildConversationMessages(ChatSession session, string newQuestion)
    {
        var messages = new List<LlmMessage>
        {
            new LlmMessage { Role = "system", Content = SystemPrompt }
        };

        // Add conversation history (last 10 messages to stay within context limits)
        var historyMessages = session.Messages.TakeLast(10);
        foreach (var msg in historyMessages)
        {
            messages.Add(new LlmMessage
            {
                Role = msg.Role,
                Content = msg.Content
            });
        }

        // Add the new question
        messages.Add(new LlmMessage { Role = "user", Content = newQuestion });

        return messages;
    }

    private List<LlmTool> BuildToolsList()
    {
        return _toolRegistry.GetAllTools().Select(tool => new LlmTool
        {
            Type = "function",
            Function = new LlmFunction
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.ParametersSchema
            }
        }).ToList();
    }

    private async Task<List<ToolCall>> ExecuteToolCallsAsync(
        List<LlmToolCall> llmToolCalls,
        ClaimsPrincipal? user)
    {
        var results = new List<ToolCall>();

        foreach (var llmCall in llmToolCalls)
        {
            var toolName = llmCall.Function.Name;
            var tool = _toolRegistry.GetTool(toolName);

            var toolCall = new ToolCall
            {
                ToolCallId = llmCall.Id,  // Preserve the tool call ID from LLM
                ToolName = toolName,
                Arguments = JsonDocument.Parse(llmCall.Function.Arguments).RootElement
            };

            if (tool == null)
            {
                _logger.LogWarning("Tool not found: {ToolName}", toolName);
                toolCall.Result = McpToolResult.Error($"Tool '{toolName}' not found");
            }
            else
            {
                _logger.LogInformation("Executing tool: {ToolName} (id: {Id}) with args: {Args}",
                    toolName, llmCall.Id, llmCall.Function.Arguments);

                toolCall.Result = await tool.ExecuteAsync(toolCall.Arguments, user);
            }

            results.Add(toolCall);
        }

        return results;
    }

    private string FormatToolResult(ToolCall toolCall)
    {
        if (toolCall.Result == null)
        {
            return "No result";
        }

        if (!toolCall.Result.Success)
        {
            return $"Error: {toolCall.Result.ErrorMessage}";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Query: {toolCall.Result.GeneratedQuery}");
        sb.AppendLine($"Found {toolCall.Result.TotalCount} results:");
        sb.AppendLine();

        switch (toolCall.Result.ResultType)
        {
            case "movies":
                foreach (var movie in toolCall.Result.Movies.Take(10))
                {
                    var title = movie.Title ?? "Unknown";
                    var year = movie.YearAsInt?.ToString() ?? "N/A";
                    var genres = movie.Genres != null ? string.Join(", ", movie.Genres) : "N/A";
                    var rating = movie.Imdb?.RatingAsDouble?.ToString("F1") ?? "N/A";

                    sb.AppendLine($"- {title} ({year}) - Genres: {genres} - IMDB: {rating}");
                }
                if (toolCall.Result.TotalCount > 10)
                {
                    sb.AppendLine($"... and {toolCall.Result.TotalCount - 10} more");
                }
                break;

            case "yearcounts":
                foreach (var yc in toolCall.Result.YearCounts.Take(20))
                {
                    sb.AppendLine($"- Year {yc.Year}: {yc.MovieCount} movies");
                }
                if (toolCall.Result.TotalCount > 20)
                {
                    sb.AppendLine($"... and {toolCall.Result.TotalCount - 20} more years");
                }
                break;

            case "export":
                sb.AppendLine($"Export created successfully!");
                sb.AppendLine($"File ID: {toolCall.Result.ExportFileId}");
                sb.AppendLine($"File Name: {toolCall.Result.ExportFileName}");
                sb.AppendLine($"Records exported: {toolCall.Result.TotalCount}");
                sb.AppendLine($"The user can download this file from the chat interface.");
                break;

            case "documents":
            default:
                foreach (var doc in toolCall.Result.Documents.Take(10))
                {
                    var title = doc.GetValue("title", BsonValue.Create("Unknown")).ToString();
                    var year = doc.GetValue("year", BsonValue.Create("N/A")).ToString();
                    var genres = doc.GetValue("genres", BsonValue.Create(new BsonArray())).ToString();

                    sb.AppendLine($"- {title} ({year}) - {genres}");
                }
                if (toolCall.Result.TotalCount > 10)
                {
                    sb.AppendLine($"... and {toolCall.Result.TotalCount - 10} more");
                }
                break;
        }

        return sb.ToString();
    }
}
