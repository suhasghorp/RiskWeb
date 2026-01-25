using System.Security.Claims;
using System.Text.Json;

namespace RiskWeb.Services.Chat;

public interface ISqlServerQueryOrchestrator
{
    Task<OrchestratorResponse> ProcessQuestionAsync(string question, ChatSession session, ClaimsPrincipal? user);
}

public class SqlServerQueryOrchestrator : ISqlServerQueryOrchestrator
{
    private readonly ILlmClient _llmClient;
    private readonly SqlServerMcpToolRegistry _toolRegistry;
    private readonly ILogger<SqlServerQueryOrchestrator> _logger;

    private const string SystemPrompt = """
        You are a helpful data assistant that answers questions about data stored in a SQL Server database.
        You have access to database exploration and query tools. Your goal is to help users find and understand their data.

        ## Available Tools

        1. **list_tables** - Lists all tables in the database. Use this first to discover what data is available.

        2. **describe_table** - Gets the schema of a specific table including:
           - Columns with data types
           - Primary keys
           - Foreign Keys (columns in THIS table that reference OTHER tables)
           - Referenced By (OTHER tables that have foreign keys pointing to THIS table)
           Use this to understand table structure and relationships before writing queries.

        3. **get_sample_data** - Gets a few sample rows from a table.
           Use this to understand what kind of data values exist in a table.

        4. **read_data** - Executes a SQL SELECT query.
           Use this after you understand the schema to retrieve specific data.

        5. **export_to_excel** - Exports SQL query results to an Excel file.
           Use this when the user asks to export, download, or save data to Excel/spreadsheet.
           This executes the query and creates an Excel file in one step.

        ## Workflow

        When answering a user's question:

        1. **Discovery Phase**: If you don't know the database structure, start by listing tables with `list_tables`.

        2. **Schema Understanding**: Use `describe_table` to understand relevant table structures.
           IMPORTANT: Pay close attention to BOTH directions of relationships:
           - "Foreign Keys" shows what this table references (parent tables)
           - "Referenced By" shows what references this table (child tables)

           Example: If user asks about data related to a "risk_event", check the risk_events table schema.
           If it shows "Referenced By: client_accounts.risk_event_id -> risk_events.id", you know
           client_accounts has a foreign key to risk_events and you can JOIN them.

        3. **Data Sampling** (optional): If needed, use `get_sample_data` to see example values.

        4. **Query Execution**: Write and execute a SQL query using `read_data` to answer the question.

        ## SQL Query Guidelines

        - Write clear, readable SQL with proper formatting
        - Use table aliases for readability (e.g., SELECT c.Name FROM Customers c)
        - Use appropriate JOINs when data spans multiple tables:
          * JOIN child tables using: parent.id = child.parent_id
          * The "Referenced By" information tells you which tables to JOIN
        - Add WHERE clauses to filter results appropriately
        - Use ORDER BY for sorted results when relevant
        - Use GROUP BY with aggregate functions (COUNT, SUM, AVG) for summaries
        - Limit results appropriately - don't return thousands of rows unless necessary

        ## Important Rules

        - NEVER make assumptions about table or column names - always verify with describe_table first
        - NEVER guess at data values - use get_sample_data if you need to understand the data
        - ALWAYS use the tools to fetch real data - never make up results
        - When asked about related data, ALWAYS check the "Referenced By" section to find child tables
        - If a query fails, examine the error and try to fix the SQL
        - If you can't answer a question with the available data, explain what's missing

        ## Response Format

        After retrieving data:
        1. Summarize the findings in a clear, conversational way
        2. If the data is tabular, describe key insights
        3. If the user asked for specific values, provide them directly
        4. Suggest follow-up queries if relevant
        """;

    public SqlServerQueryOrchestrator(
        ILlmClient llmClient,
        SqlServerMcpToolRegistry toolRegistry,
        ILogger<SqlServerQueryOrchestrator> logger)
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
        _logger.LogInformation("Processing SQL Server question: {Question}", question);

        var toolCalls = new List<ToolCall>();

        try
        {
            // Build conversation messages
            var messages = BuildConversationMessages(session, question);

            // Build tools list for LLM
            var tools = BuildToolsList();

            // Allow multiple rounds of tool calls for complex queries
            const int maxIterations = 5;
            int iteration = 0;

            while (iteration < maxIterations)
            {
                iteration++;
                _logger.LogInformation("LLM iteration {Iteration}", iteration);

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
                        ErrorMessage = response.ErrorMessage,
                        ToolCalls = toolCalls
                    };
                }

                // If no tool calls, we have our final answer
                if (response.ToolCalls == null || response.ToolCalls.Count == 0)
                {
                    return new OrchestratorResponse
                    {
                        Success = true,
                        Answer = response.Content ?? "I couldn't generate a response.",
                        ToolCalls = toolCalls
                    };
                }

                _logger.LogInformation("LLM requested {Count} tool calls", response.ToolCalls.Count);

                // Execute tool calls
                var iterationToolCalls = await ExecuteToolCallsAsync(response.ToolCalls, user);
                toolCalls.AddRange(iterationToolCalls);

                // Add assistant message with tool calls
                messages.Add(new LlmMessage
                {
                    Role = "assistant",
                    Content = response.Content,
                    ToolCalls = response.ToolCalls
                });

                // Add tool results as messages - use the preserved ToolCallId
                foreach (var toolResult in iterationToolCalls)
                {
                    var resultContent = FormatToolResult(toolResult);

                    messages.Add(new LlmMessage
                    {
                        Role = "tool",
                        Content = resultContent,
                        ToolCallId = toolResult.ToolCallId  // Use the stored ID, not lookup by name
                    });
                }
            }

            // If we've exhausted iterations, return what we have
            _logger.LogWarning("Reached max iterations without final answer");
            return new OrchestratorResponse
            {
                Success = true,
                Answer = "I've gathered some information but couldn't complete the analysis. Please try rephrasing your question.",
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

        // Add conversation history (last 10 messages for context)
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
        var sqlData = toolCall.Result.SqlData;

        switch (toolCall.Result.ResultType)
        {
            case "table_list":
                sb.AppendLine($"Found {toolCall.Result.TotalCount} tables:");
                if (sqlData?.Tables != null)
                {
                    foreach (var table in sqlData.Tables)
                    {
                        sb.AppendLine($"  - {table}");
                    }
                }
                break;

            case "table_schema":
                if (sqlData?.TableSchema != null)
                {
                    var schema = sqlData.TableSchema;
                    sb.AppendLine($"Table: {schema.SchemaName}.{schema.TableName}");

                    if (schema.Columns.Count == 0)
                    {
                        sb.AppendLine("WARNING: No columns found for this table!");
                        sb.AppendLine("This could mean:");
                        sb.AppendLine("  - The table name is misspelled");
                        sb.AppendLine("  - The table is in a different schema");
                        sb.AppendLine("  - Insufficient permissions to view the table");

                        if (schema.SimilarTableNames != null && schema.SimilarTableNames.Any())
                        {
                            sb.AppendLine();
                            sb.AppendLine("Did you mean one of these tables?");
                            foreach (var similarTable in schema.SimilarTableNames.Take(10))
                            {
                                sb.AppendLine($"  - {similarTable}");
                            }
                            sb.AppendLine();
                            sb.AppendLine("Please try describe_table with the correct table name.");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"Columns ({schema.Columns.Count}):");
                        foreach (var col in schema.Columns)
                        {
                            var pkMarker = col.IsPrimaryKey ? " [PK]" : "";
                            var nullMarker = col.IsNullable ? " (nullable)" : " (not null)";
                            var lengthInfo = col.MaxLength.HasValue ? $"({col.MaxLength})" : "";
                            sb.AppendLine($"  - {col.Name}: {col.DataType}{lengthInfo}{nullMarker}{pkMarker}");
                        }
                        if (schema.ForeignKeys.Any())
                        {
                            sb.AppendLine("Foreign Keys (this table references):");
                            foreach (var fk in schema.ForeignKeys)
                            {
                                sb.AppendLine($"  - {fk.ColumnName} -> {fk.ReferencedTable}.{fk.ReferencedColumn}");
                            }
                        }
                        if (schema.ReferencedBy.Any())
                        {
                            sb.AppendLine("Referenced By (other tables that reference this table):");
                            foreach (var refBy in schema.ReferencedBy)
                            {
                                sb.AppendLine($"  - {refBy.ReferencingTable}.{refBy.ReferencingColumn} -> {schema.TableName}.{refBy.LocalColumn}");
                            }
                        }
                    }
                }
                break;

            case "query_results":
            case "sample_data":
                sb.AppendLine($"Query: {toolCall.Result.GeneratedQuery}");
                sb.AppendLine($"Returned {toolCall.Result.TotalCount} rows:");
                if (sqlData?.Columns != null && sqlData.Rows != null)
                {
                    // Column headers
                    sb.AppendLine(string.Join(" | ", sqlData.Columns));
                    sb.AppendLine(new string('-', sqlData.Columns.Sum(c => c.Length + 3)));

                    // Data rows (limit to 20 for LLM context)
                    foreach (var row in sqlData.Rows.Take(20))
                    {
                        var values = sqlData.Columns.Select(col =>
                            row.TryGetValue(col, out var val) ? (val?.ToString() ?? "NULL") : "NULL");
                        sb.AppendLine(string.Join(" | ", values));
                    }

                    if (sqlData.Rows.Count > 20)
                    {
                        sb.AppendLine($"... and {sqlData.Rows.Count - 20} more rows");
                    }
                }
                break;

            default:
                sb.AppendLine($"Result type: {toolCall.Result.ResultType}");
                sb.AppendLine($"Total count: {toolCall.Result.TotalCount}");
                if (sqlData?.Message != null)
                {
                    sb.AppendLine(sqlData.Message);
                }
                break;
        }

        return sb.ToString();
    }
}
