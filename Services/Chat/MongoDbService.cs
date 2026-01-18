using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.Bson;
using RiskWeb.Models;

namespace RiskWeb.Services.Chat;

public interface IMongoDbService
{
    IMongoCollection<T> GetCollection<T>(string collectionName);
    IMongoCollection<BsonDocument> GetCollection(string collectionName);
    IMongoQueryable<Movie> GetMoviesQueryable();
    Task<List<Movie>> GetMoviesByGenreAsync(string genre, int limit = 10);
    Task<List<Movie>> GetMoviesByYearAsync(int year, int limit = 10);
    Task<List<Movie>> GetMoviesByYearRangeAsync(int startYear, int endYear, int limit = 10);
    Task<List<Movie>> GetMoviesByGenreAndYearAsync(string genre, int year, int limit = 10);
    Task<List<YearCount>> GetMovieCountPerYearAsync(int? startYear = null, int? endYear = null, int limit = 50);
    Task<List<string>> GetCollectionNamesAsync();
    Task<List<string>> GetDistinctGenresAsync();
    Task<List<int>> GetDistinctYearsAsync();
}

public class MongoDbService : IMongoDbService
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<MongoDbService> _logger;

    public MongoDbService(IConfiguration configuration, ILogger<MongoDbService> logger)
    {
        _logger = logger;

        var connectionString = configuration["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB connection string not configured");
        var databaseName = configuration["MongoDB:DatabaseName"]
            ?? throw new InvalidOperationException("MongoDB database name not configured");

        // Configure MongoDB to use LINQ3 provider
        var clientSettings = MongoClientSettings.FromConnectionString(connectionString);
        clientSettings.LinqProvider = LinqProvider.V3;

        var client = new MongoClient(clientSettings);
        _database = client.GetDatabase(databaseName);

        _logger.LogInformation("MongoDB service initialized for database: {DatabaseName} with LINQ V3 provider", databaseName);
    }

    public IMongoCollection<T> GetCollection<T>(string collectionName)
    {
        return _database.GetCollection<T>(collectionName);
    }

    public IMongoCollection<BsonDocument> GetCollection(string collectionName)
    {
        return _database.GetCollection<BsonDocument>(collectionName);
    }

    public IMongoQueryable<Movie> GetMoviesQueryable()
    {
        return GetCollection<Movie>("movies").AsQueryable();
    }

    public async Task<List<Movie>> GetMoviesByGenreAsync(string genre, int limit = 10)
    {
        _logger.LogInformation("LINQ Query: GetMoviesByGenre - genre={Genre}, limit={Limit}", genre, limit);

        var movies = await GetMoviesQueryable()
            .Where(m => m.Genres != null && m.Genres.Contains(genre))
            .Take(limit)
            .ToListAsync();

        _logger.LogInformation("Query returned {Count} movies", movies.Count);
        return movies;
    }

    public async Task<List<Movie>> GetMoviesByYearAsync(int year, int limit = 10)
    {
        _logger.LogInformation("LINQ Query: GetMoviesByYear - year={Year}, limit={Limit}", year, limit);

        // Use filter builder for flexible year matching (handles both int and string)
        var collection = GetCollection<Movie>("movies");
        var filter = Builders<Movie>.Filter.Or(
            Builders<Movie>.Filter.Eq(m => m.Year, year),
            Builders<Movie>.Filter.Eq(m => m.Year, year.ToString())
        );

        var movies = await collection.Find(filter).Limit(limit).ToListAsync();

        _logger.LogInformation("Query returned {Count} movies", movies.Count);
        return movies;
    }

    public async Task<List<Movie>> GetMoviesByYearRangeAsync(int startYear, int endYear, int limit = 10)
    {
        _logger.LogInformation("LINQ Query: GetMoviesByYearRange - startYear={Start}, endYear={End}, limit={Limit}",
            startYear, endYear, limit);

        var collection = GetCollection<Movie>("movies");

        // For range queries, we need to use aggregation to handle mixed types
        var pipeline = new BsonDocument[]
        {
            new BsonDocument("$addFields", new BsonDocument("numericYear",
                new BsonDocument("$convert", new BsonDocument
                {
                    { "input", "$year" },
                    { "to", "int" },
                    { "onError", BsonNull.Value },
                    { "onNull", BsonNull.Value }
                }))),
            new BsonDocument("$match", new BsonDocument("numericYear",
                new BsonDocument
                {
                    { "$gte", startYear },
                    { "$lte", endYear }
                })),
            new BsonDocument("$limit", limit)
        };

        var movies = await collection.Aggregate<Movie>(pipeline).ToListAsync();

        _logger.LogInformation("Query returned {Count} movies", movies.Count);
        return movies;
    }

    public async Task<List<Movie>> GetMoviesByGenreAndYearAsync(string genre, int year, int limit = 10)
    {
        _logger.LogInformation("Query: GetMoviesByGenreAndYear - genre={Genre}, year={Year}, limit={Limit}",
            genre, year, limit);

        var collection = GetCollection<Movie>("movies");

        // Use aggregation to handle mixed year types and filter by both genre and year
        var pipeline = new BsonDocument[]
        {
            new BsonDocument("$addFields", new BsonDocument("numericYear",
                new BsonDocument("$convert", new BsonDocument
                {
                    { "input", "$year" },
                    { "to", "int" },
                    { "onError", BsonNull.Value },
                    { "onNull", BsonNull.Value }
                }))),
            new BsonDocument("$match", new BsonDocument
            {
                { "genres", genre },
                { "numericYear", year }
            }),
            new BsonDocument("$limit", limit)
        };

        var movies = await collection.Aggregate<Movie>(pipeline).ToListAsync();

        _logger.LogInformation("Query returned {Count} movies", movies.Count);
        return movies;
    }

    public async Task<List<YearCount>> GetMovieCountPerYearAsync(int? startYear = null, int? endYear = null, int limit = 50)
    {
        _logger.LogInformation("LINQ Query: GetMovieCountPerYear - startYear={Start}, endYear={End}, limit={Limit}",
            startYear, endYear, limit);

        var collection = GetCollection<Movie>("movies");

        var pipelineStages = new List<BsonDocument>();

        // Add field to convert year to integer safely
        pipelineStages.Add(new BsonDocument("$addFields", new BsonDocument("numericYear",
            new BsonDocument("$convert", new BsonDocument
            {
                { "input", "$year" },
                { "to", "int" },
                { "onError", BsonNull.Value },
                { "onNull", BsonNull.Value }
            }))));

        // Filter out null years
        pipelineStages.Add(new BsonDocument("$match",
            new BsonDocument("numericYear", new BsonDocument("$ne", BsonNull.Value))));

        // Apply year range filter if specified
        if (startYear.HasValue || endYear.HasValue)
        {
            var matchConditions = new BsonDocument();
            if (startYear.HasValue)
                matchConditions["$gte"] = startYear.Value;
            if (endYear.HasValue)
                matchConditions["$lte"] = endYear.Value;

            pipelineStages.Add(new BsonDocument("$match",
                new BsonDocument("numericYear", matchConditions)));
        }

        // Group by year and count
        pipelineStages.Add(new BsonDocument("$group", new BsonDocument
        {
            { "_id", "$numericYear" },
            { "count", new BsonDocument("$sum", 1) }
        }));

        // Sort by year
        pipelineStages.Add(new BsonDocument("$sort", new BsonDocument("_id", 1)));

        // Limit results
        pipelineStages.Add(new BsonDocument("$limit", limit));

        // Project to final shape
        pipelineStages.Add(new BsonDocument("$project", new BsonDocument
        {
            { "_id", 0 },
            { "Year", "$_id" },
            { "MovieCount", "$count" }
        }));

        var pipelineJson = "[" + string.Join(", ", pipelineStages.Select(p => p.ToJson())) + "]";
        _logger.LogInformation("Aggregation pipeline: {Pipeline}", pipelineJson);

        var results = await collection.Aggregate<YearCount>(pipelineStages.ToArray()).ToListAsync();

        _logger.LogInformation("Aggregation returned {Count} results", results.Count);
        return results;
    }

    public async Task<List<string>> GetCollectionNamesAsync()
    {
        var cursor = await _database.ListCollectionNamesAsync();
        return await cursor.ToListAsync();
    }

    public async Task<List<string>> GetDistinctGenresAsync()
    {
        _logger.LogInformation("Getting distinct genres from movies collection");

        var collection = GetCollection<Movie>("movies");

        // Use aggregation to unwind genres array and get distinct values
        var pipeline = new BsonDocument[]
        {
            new BsonDocument("$unwind", "$genres"),
            new BsonDocument("$group", new BsonDocument("_id", "$genres")),
            new BsonDocument("$sort", new BsonDocument("_id", 1))
        };

        var results = await collection.Aggregate<BsonDocument>(pipeline).ToListAsync();
        var genres = results
            .Where(doc => doc["_id"] != BsonNull.Value && !string.IsNullOrEmpty(doc["_id"].AsString))
            .Select(doc => doc["_id"].AsString)
            .ToList();

        _logger.LogInformation("Found {Count} distinct genres", genres.Count);
        return genres;
    }

    public async Task<List<int>> GetDistinctYearsAsync()
    {
        _logger.LogInformation("Getting distinct years from movies collection");

        var collection = GetCollection<Movie>("movies");

        // Use aggregation to convert year to int and get distinct values
        var pipeline = new BsonDocument[]
        {
            new BsonDocument("$addFields", new BsonDocument("numericYear",
                new BsonDocument("$convert", new BsonDocument
                {
                    { "input", "$year" },
                    { "to", "int" },
                    { "onError", BsonNull.Value },
                    { "onNull", BsonNull.Value }
                }))),
            new BsonDocument("$match", new BsonDocument("numericYear", new BsonDocument("$ne", BsonNull.Value))),
            new BsonDocument("$group", new BsonDocument("_id", "$numericYear")),
            new BsonDocument("$sort", new BsonDocument("_id", -1)) // Sort descending (newest first)
        };

        var results = await collection.Aggregate<BsonDocument>(pipeline).ToListAsync();
        var years = results
            .Where(doc => doc["_id"] != BsonNull.Value)
            .Select(doc => doc["_id"].AsInt32)
            .ToList();

        _logger.LogInformation("Found {Count} distinct years", years.Count);
        return years;
    }
}
