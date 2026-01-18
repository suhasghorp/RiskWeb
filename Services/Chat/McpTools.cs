using System.Security.Claims;
using System.Text.Json;
using MongoDB.Bson;
using RiskWeb.Models;

namespace RiskWeb.Services.Chat;

public class FindMoviesByGenreTool : IMcpTool
{
    private readonly IMongoDbService _mongoDb;
    private readonly ILogger<FindMoviesByGenreTool> _logger;

    public string Name => "find_movies_by_genre";
    public string Description => "Find movies by genre. Returns movies that match the specified genre.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "genre": {
                    "type": "string",
                    "description": "The genre to search for (e.g., 'Action', 'Comedy', 'Drama', 'Horror', 'Short')"
                },
                "limit": {
                    "type": "integer",
                    "description": "Maximum number of results to return (default: 10, max: 50)",
                    "default": 10
                }
            },
            "required": ["genre"]
        }
        """).RootElement;

    public FindMoviesByGenreTool(IMongoDbService mongoDb, ILogger<FindMoviesByGenreTool> logger)
    {
        _mongoDb = mongoDb;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var genre = args.GetProperty("genre").GetString();
            if (string.IsNullOrWhiteSpace(genre))
            {
                return McpToolResult.Error("Genre parameter is required");
            }

            var limit = 10;
            if (args.TryGetProperty("limit", out var limitElement))
            {
                limit = Math.Min(limitElement.GetInt32(), 50);
            }

            _logger.LogInformation("Executing find_movies_by_genre: genre={Genre}, limit={Limit}", genre, limit);

            var movies = await _mongoDb.GetMoviesByGenreAsync(genre, limit);
            var queryDescription = $"LINQ: movies.Where(m => m.Genres.Contains(\"{genre}\")).Take({limit})";

            return McpToolResult.FromMovies(movies, queryDescription);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing find_movies_by_genre");
            return McpToolResult.Error($"Error executing query: {ex.Message}");
        }
    }
}

public class FindMoviesByYearTool : IMcpTool
{
    private readonly IMongoDbService _mongoDb;
    private readonly ILogger<FindMoviesByYearTool> _logger;

    public string Name => "find_movies_by_year";
    public string Description => "Find movies by release year. Can search for a specific year or a range of years.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "year": {
                    "type": "integer",
                    "description": "The specific year to search for"
                },
                "start_year": {
                    "type": "integer",
                    "description": "The start year for a range search"
                },
                "end_year": {
                    "type": "integer",
                    "description": "The end year for a range search"
                },
                "limit": {
                    "type": "integer",
                    "description": "Maximum number of results to return (default: 10, max: 50)",
                    "default": 10
                }
            }
        }
        """).RootElement;

    public FindMoviesByYearTool(IMongoDbService mongoDb, ILogger<FindMoviesByYearTool> logger)
    {
        _mongoDb = mongoDb;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var limit = 10;
            if (args.TryGetProperty("limit", out var limitElement))
            {
                limit = Math.Min(limitElement.GetInt32(), 50);
            }

            List<Movie> movies;
            string queryDescription;

            if (args.TryGetProperty("year", out var yearElement))
            {
                var year = yearElement.GetInt32();
                _logger.LogInformation("Executing find_movies_by_year: year={Year}, limit={Limit}", year, limit);

                movies = await _mongoDb.GetMoviesByYearAsync(year, limit);
                queryDescription = $"LINQ: movies.Where(m => m.Year == {year}).Take({limit})";
            }
            else if (args.TryGetProperty("start_year", out var startElement) &&
                     args.TryGetProperty("end_year", out var endElement))
            {
                var startYear = startElement.GetInt32();
                var endYear = endElement.GetInt32();
                _logger.LogInformation("Executing find_movies_by_year: range={Start}-{End}, limit={Limit}",
                    startYear, endYear, limit);

                movies = await _mongoDb.GetMoviesByYearRangeAsync(startYear, endYear, limit);
                queryDescription = $"LINQ: movies.Where(m => m.Year >= {startYear} && m.Year <= {endYear}).Take({limit})";
            }
            else
            {
                return McpToolResult.Error("Either 'year' or both 'start_year' and 'end_year' must be provided");
            }

            return McpToolResult.FromMovies(movies, queryDescription);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing find_movies_by_year");
            return McpToolResult.Error($"Error executing query: {ex.Message}");
        }
    }
}

public class FindMoviesByGenreAndYearTool : IMcpTool
{
    private readonly IMongoDbService _mongoDb;
    private readonly ILogger<FindMoviesByGenreAndYearTool> _logger;

    public string Name => "find_movies_by_genre_and_year";
    public string Description => "Find movies by BOTH genre AND year. Use this when the user wants to filter by genre and year together (e.g., 'action movies from 2020', 'comedy films in 1995').";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "genre": {
                    "type": "string",
                    "description": "The genre to search for (e.g., 'Action', 'Comedy', 'Drama')"
                },
                "year": {
                    "type": "integer",
                    "description": "The year to filter by"
                },
                "limit": {
                    "type": "integer",
                    "description": "Maximum number of results to return. Use 0 for ALL results. Default: 10",
                    "default": 10
                }
            },
            "required": ["genre", "year"]
        }
        """).RootElement;

    public FindMoviesByGenreAndYearTool(IMongoDbService mongoDb, ILogger<FindMoviesByGenreAndYearTool> logger)
    {
        _mongoDb = mongoDb;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var genre = args.GetProperty("genre").GetString();
            if (string.IsNullOrWhiteSpace(genre))
            {
                return McpToolResult.Error("Genre parameter is required");
            }

            var year = args.GetProperty("year").GetInt32();

            var requestedLimit = 10;
            if (args.TryGetProperty("limit", out var limitElement))
            {
                requestedLimit = limitElement.GetInt32();
            }
            var limit = requestedLimit <= 0 ? 50000 : Math.Min(requestedLimit, 50000);

            _logger.LogInformation("Executing find_movies_by_genre_and_year: genre={Genre}, year={Year}, limit={Limit}",
                genre, year, limit);

            var movies = await _mongoDb.GetMoviesByGenreAndYearAsync(genre, year, limit);
            var queryDescription = $"LINQ: movies.Where(m => m.Genres.Contains(\"{genre}\") && m.Year == {year}).Take({limit})";

            return McpToolResult.FromMovies(movies, queryDescription);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing find_movies_by_genre_and_year");
            return McpToolResult.Error($"Error executing query: {ex.Message}");
        }
    }
}

public class CountMoviesPerYearTool : IMcpTool
{
    private readonly IMongoDbService _mongoDb;
    private readonly ILogger<CountMoviesPerYearTool> _logger;

    public string Name => "count_movies_per_year";
    public string Description => "Get the count of movies available per year. Can return counts for all years, a specific year, or a range of years. Results are sorted by year.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "start_year": {
                    "type": "integer",
                    "description": "Optional start year to filter results (e.g., 1990)"
                },
                "end_year": {
                    "type": "integer",
                    "description": "Optional end year to filter results (e.g., 2020)"
                },
                "limit": {
                    "type": "integer",
                    "description": "Maximum number of year results to return (default: 50, max: 100)",
                    "default": 50
                }
            }
        }
        """).RootElement;

    public CountMoviesPerYearTool(IMongoDbService mongoDb, ILogger<CountMoviesPerYearTool> logger)
    {
        _mongoDb = mongoDb;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var limit = 50;
            if (args.TryGetProperty("limit", out var limitElement))
            {
                limit = Math.Min(limitElement.GetInt32(), 100);
            }

            int? startYear = null;
            int? endYear = null;

            if (args.TryGetProperty("start_year", out var startElement))
            {
                startYear = startElement.GetInt32();
            }
            if (args.TryGetProperty("end_year", out var endElement))
            {
                endYear = endElement.GetInt32();
            }

            _logger.LogInformation("Executing count_movies_per_year: startYear={Start}, endYear={End}, limit={Limit}",
                startYear, endYear, limit);

            var results = await _mongoDb.GetMovieCountPerYearAsync(startYear, endYear, limit);

            var queryDescription = startYear.HasValue || endYear.HasValue
                ? $"LINQ: movies.Where(m => m.Year >= {startYear ?? 0} && m.Year <= {endYear ?? 9999}).GroupBy(m => m.Year).Select(g => new {{ Year = g.Key, Count = g.Count() }}).OrderBy(x => x.Year).Take({limit})"
                : $"LINQ: movies.GroupBy(m => m.Year).Select(g => new {{ Year = g.Key, Count = g.Count() }}).OrderBy(x => x.Year).Take({limit})";

            _logger.LogInformation("count_movies_per_year returned {Count} results", results.Count);

            return McpToolResult.FromYearCounts(results, queryDescription);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing count_movies_per_year");
            return McpToolResult.Error($"Error executing query: {ex.Message}");
        }
    }
}

public class ExportResultsToExcelTool : IMcpTool
{
    private readonly IMongoDbService _mongoDb;
    private readonly IExcelExportService _excelExport;
    private readonly ILogger<ExportResultsToExcelTool> _logger;

    public string Name => "export_to_excel";
    public string Description => "Export movie query results to an Excel file. Use this DIRECTLY when the user asks to export, download, or save results to Excel/spreadsheet. Do NOT run a search first - just call this tool with the appropriate parameters.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "export_type": {
                    "type": "string",
                    "enum": ["movies_by_genre", "movies_by_year", "movies_by_year_range", "movies_by_genre_and_year", "movie_counts"],
                    "description": "The type of data to export. Use 'movies_by_genre_and_year' when filtering by BOTH genre AND year."
                },
                "genre": {
                    "type": "string",
                    "description": "Genre filter (required for movies_by_genre and movies_by_genre_and_year)"
                },
                "year": {
                    "type": "integer",
                    "description": "Year filter (required for movies_by_year and movies_by_genre_and_year)"
                },
                "start_year": {
                    "type": "integer",
                    "description": "Start year for range exports"
                },
                "end_year": {
                    "type": "integer",
                    "description": "End year for range exports"
                },
                "limit": {
                    "type": "integer",
                    "description": "Maximum records to export. Use 0 for ALL records. Default: 100. IMPORTANT: When user says 'all' or 'everything', set limit to 0.",
                    "default": 100
                }
            },
            "required": ["export_type"]
        }
        """).RootElement;

    public ExportResultsToExcelTool(
        IMongoDbService mongoDb,
        IExcelExportService excelExport,
        ILogger<ExportResultsToExcelTool> logger)
    {
        _mongoDb = mongoDb;
        _excelExport = excelExport;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var exportType = args.GetProperty("export_type").GetString();

            // Handle limit: 0 means "all records", otherwise use the provided value (max 50000 for safety)
            var requestedLimit = args.TryGetProperty("limit", out var limitEl) ? limitEl.GetInt32() : 100;
            var limit = requestedLimit <= 0 ? 50000 : Math.Min(requestedLimit, 50000);
            var isAllRecords = requestedLimit <= 0;

            _logger.LogInformation("Executing export_to_excel: type={Type}, limit={Limit}, isAll={IsAll}",
                exportType, limit, isAllRecords);

            ExportResult exportResult;
            string queryDescription;

            switch (exportType)
            {
                case "movies_by_genre":
                    if (!args.TryGetProperty("genre", out var genreEl))
                        return McpToolResult.Error("Genre is required for movies_by_genre export");

                    var genre = genreEl.GetString()!;
                    var moviesByGenre = await _mongoDb.GetMoviesByGenreAsync(genre, limit);
                    queryDescription = isAllRecords
                        ? $"All movies by genre: {genre}"
                        : $"Movies by genre: {genre} (limit: {limit})";
                    exportResult = await _excelExport.ExportMoviesToExcelAsync(moviesByGenre, queryDescription);
                    break;

                case "movies_by_year":
                    if (!args.TryGetProperty("year", out var yearEl))
                        return McpToolResult.Error("Year is required for movies_by_year export");

                    var year = yearEl.GetInt32();
                    var moviesByYear = await _mongoDb.GetMoviesByYearAsync(year, limit);
                    queryDescription = isAllRecords
                        ? $"All movies from year: {year}"
                        : $"Movies from year: {year} (limit: {limit})";
                    exportResult = await _excelExport.ExportMoviesToExcelAsync(moviesByYear, queryDescription);
                    break;

                case "movies_by_year_range":
                    if (!args.TryGetProperty("start_year", out var startEl) || !args.TryGetProperty("end_year", out var endEl))
                        return McpToolResult.Error("start_year and end_year are required for movies_by_year_range export");

                    var startYear = startEl.GetInt32();
                    var endYear = endEl.GetInt32();
                    var moviesByRange = await _mongoDb.GetMoviesByYearRangeAsync(startYear, endYear, limit);
                    queryDescription = isAllRecords
                        ? $"All movies from {startYear} to {endYear}"
                        : $"Movies from {startYear} to {endYear} (limit: {limit})";
                    exportResult = await _excelExport.ExportMoviesToExcelAsync(moviesByRange, queryDescription);
                    break;

                case "movies_by_genre_and_year":
                    if (!args.TryGetProperty("genre", out var gyGenreEl))
                        return McpToolResult.Error("Genre is required for movies_by_genre_and_year export");
                    if (!args.TryGetProperty("year", out var gyYearEl))
                        return McpToolResult.Error("Year is required for movies_by_genre_and_year export");

                    var gyGenre = gyGenreEl.GetString()!;
                    var gyYear = gyYearEl.GetInt32();
                    var moviesByGenreAndYear = await _mongoDb.GetMoviesByGenreAndYearAsync(gyGenre, gyYear, limit);
                    queryDescription = isAllRecords
                        ? $"All {gyGenre} movies from {gyYear}"
                        : $"{gyGenre} movies from {gyYear} (limit: {limit})";
                    exportResult = await _excelExport.ExportMoviesToExcelAsync(moviesByGenreAndYear, queryDescription);
                    break;

                case "movie_counts":
                    int? countStartYear = args.TryGetProperty("start_year", out var csEl) ? csEl.GetInt32() : null;
                    int? countEndYear = args.TryGetProperty("end_year", out var ceEl) ? ceEl.GetInt32() : null;
                    var yearCounts = await _mongoDb.GetMovieCountPerYearAsync(countStartYear, countEndYear, limit);
                    queryDescription = countStartYear.HasValue || countEndYear.HasValue
                        ? $"Movie counts by year ({countStartYear ?? 0} to {countEndYear ?? 9999})"
                        : "Movie counts by year (all years)";
                    exportResult = await _excelExport.ExportYearCountsToExcelAsync(yearCounts, queryDescription);
                    break;

                default:
                    return McpToolResult.Error($"Unknown export type: {exportType}");
            }

            if (!exportResult.Success)
            {
                return McpToolResult.Error(exportResult.ErrorMessage ?? "Export failed");
            }

            _logger.LogInformation("Export successful: {FileName}, {RecordCount} records",
                exportResult.FileName, exportResult.RecordCount);

            return McpToolResult.FromExport(
                exportResult.FileId!,
                exportResult.FileName!,
                exportResult.RecordCount,
                queryDescription);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing export_to_excel");
            return McpToolResult.Error($"Export error: {ex.Message}");
        }
    }
}

public class McpToolRegistry
{
    private readonly Dictionary<string, IMcpTool> _tools = new();
    private readonly ILogger<McpToolRegistry> _logger;

    public McpToolRegistry(
        FindMoviesByGenreTool findByGenre,
        FindMoviesByYearTool findByYear,
        FindMoviesByGenreAndYearTool findByGenreAndYear,
        CountMoviesPerYearTool countPerYear,
        ExportResultsToExcelTool exportToExcel,
        ILogger<McpToolRegistry> logger)
    {
        _logger = logger;

        RegisterTool(findByGenre);
        RegisterTool(findByYear);
        RegisterTool(findByGenreAndYear);
        RegisterTool(countPerYear);
        RegisterTool(exportToExcel);

        _logger.LogInformation("MCP Tool Registry initialized with {Count} tools", _tools.Count);
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
