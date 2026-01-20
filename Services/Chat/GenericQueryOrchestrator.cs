using System.Security.Claims;
using System.Text.Json;
using MongoDB.Bson;

namespace RiskWeb.Services.Chat;

public class GenericQueryOrchestrator : IQueryOrchestrator
{
    private readonly ILlmClient _llmClient;
    private readonly GenericToolRegistry _toolRegistry;
    private readonly ILogger<GenericQueryOrchestrator> _logger;

    private const string SystemPrompt = """
        You are a helpful assistant that queries a MongoDB movie database.

        Database: mflix
        Collection: movies

        Document structure:
        {
          "_id": ObjectId,
          "title": string,
          "year": number (note: some documents may have year as string),
          "genres": [string] (e.g., ["Action", "Comedy", "Drama"]),
          "directors": [string],
          "cast": [string],
          "plot": string,
          "fullplot": string,
          "runtime": number (minutes),
          "rated": string (e.g., "PG", "R", "PG-13"),
          "imdb": { "rating": number, "votes": number, "id": number },
          "awards": { "wins": number, "nominations": number, "text": string },
          "countries": [string],
          "languages": [string],
          "released": date,
          "poster": string (URL),
          "metacritic": number
        }

        Available tools:

        1. execute_mongodb_query - Execute MongoDB queries
           - collection: "movies"
           - operation: "find" | "aggregate" | "count"
           - query: MongoDB filter object (for find/count) or pipeline array (for aggregate)
           - options: { limit, skip, sort, projection }

        2. export_to_excel - Export results to Excel file
           - Use for "export to Excel", "download as spreadsheet", "save to file" requests

        MongoDB Query Examples:

        Simple find (Action movies):
        {
          "collection": "movies",
          "operation": "find",
          "query": { "genres": "Action" },
          "options": { "limit": 10 }
        }

        Find with year filter (movies from 2020):
        {
          "collection": "movies",
          "operation": "find",
          "query": { "year": 2020 },
          "options": { "limit": 10 }
        }

        Combined filter (Action movies from 2015):
        {
          "collection": "movies",
          "operation": "find",
          "query": { "genres": "Action", "year": 2015 },
          "options": { "limit": 10 }
        }

        Year range filter (movies from 2000-2010):
        {
          "collection": "movies",
          "operation": "find",
          "query": { "year": { "$gte": 2000, "$lte": 2010 } },
          "options": { "limit": 10, "sort": { "year": 1 } }
        }

        High-rated movies (IMDB > 8):
        {
          "collection": "movies",
          "operation": "find",
          "query": { "imdb.rating": { "$gte": 8 } },
          "options": { "limit": 10, "sort": { "imdb.rating": -1 } }
        }

        Count movies per year (aggregation):
        {
          "collection": "movies",
          "operation": "aggregate",
          "query": [
            { "$group": { "_id": "$year", "count": { "$sum": 1 } } },
            { "$sort": { "_id": 1 } }
          ]
        }

        Count movies per year in range:
        {
          "collection": "movies",
          "operation": "aggregate",
          "query": [
            { "$match": { "year": { "$gte": 2000, "$lte": 2020 } } },
            { "$group": { "_id": "$year", "count": { "$sum": 1 } } },
            { "$sort": { "_id": 1 } }
          ]
        }

        Top genres by movie count:
        {
          "collection": "movies",
          "operation": "aggregate",
          "query": [
            { "$unwind": "$genres" },
            { "$group": { "_id": "$genres", "count": { "$sum": 1 } } },
            { "$sort": { "count": -1 } },
            { "$limit": 10 }
          ]
        }

        Count total documents:
        {
          "collection": "movies",
          "operation": "count",
          "query": {}
        }

        Count with filter:
        {
          "collection": "movies",
          "operation": "count",
          "query": { "genres": "Drama" }
        }

        Important guidelines:
        - Always use tools to fetch data - never make up movie information
        - Use "find" for simple queries returning movie documents
        - Use "aggregate" for grouping, counting per category, or complex transformations
        - Use "count" when user asks "how many" without needing the actual documents
        - When user says "all", increase the limit appropriately (e.g., 1000)
        - After receiving tool results, summarize the findings in a helpful way
        - If no results are found, suggest alternative search criteria
        - Keep responses concise and focused on the query results
        - For export requests, call export_to_excel directly with appropriate parameters
        """;

    public GenericQueryOrchestrator(
        ILlmClient llmClient,
        GenericToolRegistry toolRegistry,
        ILogger<GenericQueryOrchestrator> logger)
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
        _logger.LogInformation("Processing question with generic orchestrator: {Question}", question);

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

                // Add tool results as messages
                foreach (var toolResult in toolResults)
                {
                    var resultContent = FormatToolResult(toolResult);
                    messages.Add(new LlmMessage
                    {
                        Role = "tool",
                        Content = resultContent,
                        ToolCallId = toolResult.ToolCallId
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
                Answer = response.Content ?? "I'm not sure how to help with that. Try asking about movies by genre, year, or requesting aggregations.",
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
                ToolCallId = llmCall.Id,
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
                _logger.LogInformation("Executing tool: {ToolName} with args: {Args}",
                    toolName, llmCall.Function.Arguments);

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

            case "count":
                sb.AppendLine($"Total count: {toolCall.Result.TotalCount}");
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
