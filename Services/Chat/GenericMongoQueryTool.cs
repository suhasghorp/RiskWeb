using System.Security.Claims;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using RiskWeb.Models;

namespace RiskWeb.Services.Chat;

public class GenericMongoQueryTool : IMcpTool
{
    private readonly IMongoDbService _mongoDb;
    private readonly ILogger<GenericMongoQueryTool> _logger;

    public string Name => "execute_mongodb_query";
    public string Description => """
        Execute a MongoDB query on the movie database. Supports find, aggregate, and count operations.
        Use this tool to query the movies collection with any valid MongoDB filter or aggregation pipeline.
        """;

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "collection": {
                    "type": "string",
                    "description": "MongoDB collection name (e.g., 'movies')"
                },
                "operation": {
                    "type": "string",
                    "enum": ["find", "aggregate", "count"],
                    "description": "MongoDB operation type: 'find' for simple queries, 'aggregate' for complex/grouping operations, 'count' for counting documents"
                },
                "query": {
                    "type": "object",
                    "description": "MongoDB filter (for find/count) or pipeline array (for aggregate). For find: use filter object like {\"genres\": \"Action\", \"year\": 2020}. For aggregate: use pipeline array like [{\"$match\": {...}}, {\"$group\": {...}}]"
                },
                "options": {
                    "type": "object",
                    "description": "Optional query options",
                    "properties": {
                        "limit": {
                            "type": "integer",
                            "description": "Maximum number of results to return (default: 10, max: 1000)",
                            "default": 10
                        },
                        "skip": {
                            "type": "integer",
                            "description": "Number of documents to skip (default: 0)",
                            "default": 0
                        },
                        "sort": {
                            "type": "object",
                            "description": "Sort specification (e.g., {\"year\": -1} for descending by year)"
                        },
                        "projection": {
                            "type": "object",
                            "description": "Fields to include/exclude (e.g., {\"title\": 1, \"year\": 1})"
                        }
                    }
                }
            },
            "required": ["collection", "operation", "query"]
        }
        """).RootElement;

    public GenericMongoQueryTool(IMongoDbService mongoDb, ILogger<GenericMongoQueryTool> logger)
    {
        _mongoDb = mongoDb;
        _logger = logger;
    }

    public async Task<McpToolResult> ExecuteAsync(JsonElement args, ClaimsPrincipal? user)
    {
        try
        {
            var collection = args.GetProperty("collection").GetString();
            var operation = args.GetProperty("operation").GetString();
            var query = args.GetProperty("query");

            if (string.IsNullOrWhiteSpace(collection))
            {
                return McpToolResult.Error("Collection parameter is required");
            }

            if (string.IsNullOrWhiteSpace(operation))
            {
                return McpToolResult.Error("Operation parameter is required");
            }

            // Parse options
            var limit = 10;
            var skip = 0;
            BsonDocument? sort = null;
            BsonDocument? projection = null;

            if (args.TryGetProperty("options", out var options))
            {
                if (options.TryGetProperty("limit", out var limitEl))
                {
                    limit = Math.Min(Math.Max(limitEl.GetInt32(), 1), 1000);
                }
                if (options.TryGetProperty("skip", out var skipEl))
                {
                    skip = Math.Max(skipEl.GetInt32(), 0);
                }
                if (options.TryGetProperty("sort", out var sortEl))
                {
                    sort = BsonDocument.Parse(sortEl.GetRawText());
                }
                if (options.TryGetProperty("projection", out var projEl))
                {
                    projection = BsonDocument.Parse(projEl.GetRawText());
                }
            }

            var queryJson = query.GetRawText();
            _logger.LogInformation("Executing {Operation} on {Collection}: query={Query}, limit={Limit}",
                operation, collection, queryJson, limit);

            return operation?.ToLower() switch
            {
                "find" => await ExecuteFindAsync(collection!, queryJson, limit, skip, sort, projection),
                "aggregate" => await ExecuteAggregateAsync(collection!, queryJson, limit),
                "count" => await ExecuteCountAsync(collection!, queryJson),
                _ => McpToolResult.Error($"Unknown operation: {operation}. Supported: find, aggregate, count")
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error in execute_mongodb_query");
            return McpToolResult.Error($"Invalid JSON in query: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing execute_mongodb_query");
            return McpToolResult.Error($"Error executing query: {ex.Message}");
        }
    }

    private async Task<McpToolResult> ExecuteFindAsync(
        string collectionName,
        string filterJson,
        int limit,
        int skip,
        BsonDocument? sort,
        BsonDocument? projection)
    {
        var filter = BsonDocument.Parse(filterJson);

        // Check if the filter involves year - if so, we need to handle mixed types
        if (FilterInvolvesYear(filter))
        {
            return await ExecuteFindWithYearHandlingAsync(collectionName, filter, limit, skip, sort, projection);
        }

        var collection = _mongoDb.GetCollection(collectionName);

        // Use Filter builder for array matching (properly handles array contains)
        FilterDefinition<BsonDocument> filterDef;
        if (filter.Contains("genres") && filter["genres"].IsString)
        {
            var genreValue = filter["genres"].AsString;
            var otherFilters = new BsonDocument(filter.Where(e => e.Name != "genres"));

            filterDef = Builders<BsonDocument>.Filter.AnyEq("genres", genreValue);
            if (otherFilters.ElementCount > 0)
            {
                filterDef = Builders<BsonDocument>.Filter.And(filterDef, otherFilters);
            }
        }
        else
        {
            filterDef = filter;
        }

        var findFluent = collection.Find(filterDef);

        if (sort != null)
        {
            findFluent = findFluent.Sort(sort);
        }

        if (skip > 0)
        {
            findFluent = findFluent.Skip(skip);
        }

        findFluent = findFluent.Limit(limit);

        if (projection != null)
        {
            findFluent = findFluent.Project<BsonDocument>(projection);
        }

        var documents = await findFluent.ToListAsync();

        _logger.LogInformation("ExecuteFindAsync: Returned {Count} documents for filter {Filter}", documents.Count, filterJson);

        // Deserialize to Movie objects
        var movies = documents.Select(doc =>
        {
            try
            {
                return BsonSerializer.Deserialize<Movie>(doc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize document: {DocId}", doc.GetValue("_id", "unknown"));
                return null;
            }
        }).Where(m => m != null).Cast<Movie>().ToList();

        var queryDescription = $"db.{collectionName}.find({filterJson})";
        if (sort != null) queryDescription += $".sort({sort.ToJson()})";
        if (skip > 0) queryDescription += $".skip({skip})";
        queryDescription += $".limit({limit})";

        return McpToolResult.FromMovies(movies, queryDescription);
    }

    private async Task<McpToolResult> ExecuteFindWithYearHandlingAsync(
        string collectionName,
        BsonDocument filter,
        int limit,
        int skip,
        BsonDocument? sort,
        BsonDocument? projection)
    {
        var collection = _mongoDb.GetCollection<Movie>(collectionName);

        // Build aggregation pipeline to handle mixed year types
        var pipeline = new List<BsonDocument>();

        // Add field to convert year to numeric
        pipeline.Add(new BsonDocument("$addFields", new BsonDocument("numericYear",
            new BsonDocument("$convert", new BsonDocument
            {
                { "input", "$year" },
                { "to", "int" },
                { "onError", BsonNull.Value },
                { "onNull", BsonNull.Value }
            }))));

        // Transform the filter to use numericYear instead of year
        var transformedFilter = TransformYearFilter(filter);
        pipeline.Add(new BsonDocument("$match", transformedFilter));

        // Add sort if specified
        if (sort != null)
        {
            // Transform sort to use numericYear if sorting by year
            var transformedSort = TransformYearSort(sort);
            pipeline.Add(new BsonDocument("$sort", transformedSort));
        }

        // Add skip
        if (skip > 0)
        {
            pipeline.Add(new BsonDocument("$skip", skip));
        }

        // Add limit
        pipeline.Add(new BsonDocument("$limit", limit));

        // Add projection if specified
        if (projection != null)
        {
            pipeline.Add(new BsonDocument("$project", projection));
        }

        var movies = await collection.Aggregate<Movie>(pipeline.ToArray()).ToListAsync();

        var queryDescription = $"db.{collectionName}.aggregate([year-safe pipeline]) /* Original filter: {filter.ToJson()} */";
        _logger.LogInformation("Find with year handling returned {Count} documents", movies.Count);
        return McpToolResult.FromMovies(movies, queryDescription);
    }

    private async Task<McpToolResult> ExecuteAggregateAsync(string collectionName, string pipelineJson, int limit)
    {
        var collection = _mongoDb.GetCollection<BsonDocument>(collectionName);

        // Parse the pipeline - it should be an array
        BsonArray pipelineArray;
        try
        {
            var parsed = BsonSerializer.Deserialize<BsonValue>(pipelineJson);
            if (parsed is BsonArray arr)
            {
                pipelineArray = arr;
            }
            else if (parsed is BsonDocument doc)
            {
                // Single stage, wrap in array
                pipelineArray = new BsonArray { doc };
            }
            else
            {
                return McpToolResult.Error("Aggregate query must be an array of pipeline stages");
            }
        }
        catch (Exception ex)
        {
            return McpToolResult.Error($"Failed to parse aggregation pipeline: {ex.Message}");
        }

        var pipeline = pipelineArray.Select(stage => stage.AsBsonDocument).ToList();

        // Check if pipeline involves year and needs transformation
        if (PipelineInvolvesYear(pipeline))
        {
            pipeline = TransformPipelineForYear(pipeline);
        }

        // Add a limit at the end if not already present
        if (!pipeline.Any(stage => stage.Contains("$limit")))
        {
            pipeline.Add(new BsonDocument("$limit", limit));
        }

        var results = await collection.Aggregate<BsonDocument>(pipeline.ToArray()).ToListAsync();

        var queryDescription = $"db.{collectionName}.aggregate({pipelineJson})";
        _logger.LogInformation("Aggregate query returned {Count} documents", results.Count);

        // Check if this looks like a year count aggregation
        if (IsYearCountAggregation(results))
        {
            var yearCounts = results.Select(doc =>
            {
                var yearValue = doc.GetValue("_id", BsonNull.Value);
                var countValue = doc.GetValue("count", doc.GetValue("MovieCount", BsonValue.Create(0)));

                int year = 0;
                if (yearValue.IsInt32) year = yearValue.AsInt32;
                else if (yearValue.IsInt64) year = (int)yearValue.AsInt64;
                else if (yearValue.IsDouble) year = (int)yearValue.AsDouble;

                int count = 0;
                if (countValue.IsInt32) count = countValue.AsInt32;
                else if (countValue.IsInt64) count = (int)countValue.AsInt64;

                return new YearCount { Year = year, MovieCount = count };
            }).Where(yc => yc.Year > 0).ToList();

            return McpToolResult.FromYearCounts(yearCounts, queryDescription);
        }

        // Try to convert to movies if the results look like movie documents
        if (results.Any() && results[0].Contains("title"))
        {
            try
            {
                var movies = results.Select(doc => BsonSerializer.Deserialize<Movie>(doc)).ToList();
                return McpToolResult.FromMovies(movies, queryDescription);
            }
            catch
            {
                // Fall back to raw documents
            }
        }

        return McpToolResult.Ok(results, queryDescription);
    }

    private async Task<McpToolResult> ExecuteCountAsync(string collectionName, string filterJson)
    {
        var collection = _mongoDb.GetCollection<BsonDocument>(collectionName);
        var filter = BsonDocument.Parse(filterJson);

        // Check if the filter involves year
        if (FilterInvolvesYear(filter))
        {
            // Use aggregation for accurate count with mixed year types
            var pipeline = new List<BsonDocument>
            {
                new BsonDocument("$addFields", new BsonDocument("numericYear",
                    new BsonDocument("$convert", new BsonDocument
                    {
                        { "input", "$year" },
                        { "to", "int" },
                        { "onError", BsonNull.Value },
                        { "onNull", BsonNull.Value }
                    }))),
                new BsonDocument("$match", TransformYearFilter(filter)),
                new BsonDocument("$count", "total")
            };

            var results = await collection.Aggregate<BsonDocument>(pipeline.ToArray()).ToListAsync();
            var count = results.FirstOrDefault()?.GetValue("total", 0).ToInt32() ?? 0;

            var queryDescription = $"db.{collectionName}.countDocuments({filterJson}) /* with year type handling */";
            _logger.LogInformation("Count query (with year handling) returned {Count}", count);

            return new McpToolResult
            {
                Success = true,
                TotalCount = count,
                GeneratedQuery = queryDescription,
                ResultType = "count"
            };
        }

        var countResult = await collection.CountDocumentsAsync(new BsonDocumentFilterDefinition<BsonDocument>(filter));

        var description = $"db.{collectionName}.countDocuments({filterJson})";
        _logger.LogInformation("Count query returned {Count}", countResult);

        return new McpToolResult
        {
            Success = true,
            TotalCount = (int)countResult,
            GeneratedQuery = description,
            ResultType = "count"
        };
    }

    private bool FilterInvolvesYear(BsonDocument filter)
    {
        return filter.Contains("year") ||
               filter.Contains("numericYear") ||
               filter.ToString().Contains("\"year\"");
    }

    private bool PipelineInvolvesYear(List<BsonDocument> pipeline)
    {
        return pipeline.Any(stage => stage.ToString().Contains("\"year\"") || stage.ToString().Contains("$year"));
    }

    private BsonDocument TransformYearFilter(BsonDocument filter)
    {
        var transformed = new BsonDocument();

        foreach (var element in filter)
        {
            if (element.Name == "year")
            {
                // Replace year with numericYear
                transformed.Add("numericYear", element.Value);
            }
            else if (element.Value is BsonDocument nested)
            {
                transformed.Add(element.Name, TransformYearFilter(nested));
            }
            else if (element.Value is BsonArray arr)
            {
                var transformedArray = new BsonArray();
                foreach (var item in arr)
                {
                    if (item is BsonDocument doc)
                    {
                        transformedArray.Add(TransformYearFilter(doc));
                    }
                    else
                    {
                        transformedArray.Add(item);
                    }
                }
                transformed.Add(element.Name, transformedArray);
            }
            else
            {
                transformed.Add(element.Name, element.Value);
            }
        }

        return transformed;
    }

    private BsonDocument TransformYearSort(BsonDocument sort)
    {
        var transformed = new BsonDocument();

        foreach (var element in sort)
        {
            if (element.Name == "year")
            {
                transformed.Add("numericYear", element.Value);
            }
            else
            {
                transformed.Add(element.Name, element.Value);
            }
        }

        return transformed;
    }

    private List<BsonDocument> TransformPipelineForYear(List<BsonDocument> pipeline)
    {
        var transformed = new List<BsonDocument>();
        var addedYearConversion = false;

        foreach (var stage in pipeline)
        {
            // Check if this stage references year and we haven't added conversion yet
            if (!addedYearConversion && stage.ToString().Contains("\"year\""))
            {
                // Insert year conversion stage before this one
                transformed.Add(new BsonDocument("$addFields", new BsonDocument("numericYear",
                    new BsonDocument("$convert", new BsonDocument
                    {
                        { "input", "$year" },
                        { "to", "int" },
                        { "onError", BsonNull.Value },
                        { "onNull", BsonNull.Value }
                    }))));
                addedYearConversion = true;
            }

            // Transform the stage to use numericYear
            if (stage.Contains("$match"))
            {
                var match = stage["$match"].AsBsonDocument;
                transformed.Add(new BsonDocument("$match", TransformYearFilter(match)));
            }
            else if (stage.Contains("$sort"))
            {
                var sort = stage["$sort"].AsBsonDocument;
                transformed.Add(new BsonDocument("$sort", TransformYearSort(sort)));
            }
            else if (stage.Contains("$group"))
            {
                var group = stage["$group"].AsBsonDocument;
                var transformedGroup = new BsonDocument();
                foreach (var element in group)
                {
                    if (element.Name == "_id" && element.Value.ToString() == "$year")
                    {
                        transformedGroup.Add("_id", "$numericYear");
                    }
                    else
                    {
                        transformedGroup.Add(element.Name, element.Value);
                    }
                }
                transformed.Add(new BsonDocument("$group", transformedGroup));
            }
            else
            {
                transformed.Add(stage);
            }
        }

        return transformed;
    }

    private bool IsYearCountAggregation(List<BsonDocument> results)
    {
        if (!results.Any()) return false;

        var first = results[0];
        return first.Contains("_id") &&
               (first.Contains("count") || first.Contains("MovieCount")) &&
               first.ElementCount <= 3;
    }
}
