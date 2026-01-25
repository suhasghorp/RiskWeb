using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RiskWeb.Services.Chat;

public interface ISqlServerMcpService
{
    Task<List<string>> GetTableNamesAsync();
    Task<TableSchema> GetTableSchemaAsync(string tableName);
    Task<QueryResult> ExecuteQueryAsync(string query, int maxRows = 100);
    Task<List<Dictionary<string, object?>>> GetSampleDataAsync(string tableName, int sampleSize = 5);
}

public class TableSchema
{
    public string TableName { get; set; } = string.Empty;
    public string? SchemaName { get; set; }
    public List<ColumnInfo> Columns { get; set; } = new();
    public List<string> PrimaryKeys { get; set; } = new();
    public List<ForeignKeyInfo> ForeignKeys { get; set; } = new();
    /// <summary>
    /// Tables that reference this table via foreign keys (reverse relationships)
    /// </summary>
    public List<ReferencedByInfo> ReferencedBy { get; set; } = new();
    /// <summary>
    /// If no columns found, this contains similar table names that might be what the user meant
    /// </summary>
    public List<string>? SimilarTableNames { get; set; }
}

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public int? MaxLength { get; set; }
    public bool IsPrimaryKey { get; set; }
}

public class ForeignKeyInfo
{
    public string ColumnName { get; set; } = string.Empty;
    public string ReferencedTable { get; set; } = string.Empty;
    public string ReferencedColumn { get; set; } = string.Empty;
}

/// <summary>
/// Represents a table that references this table via a foreign key
/// </summary>
public class ReferencedByInfo
{
    public string ReferencingTable { get; set; } = string.Empty;
    public string ReferencingColumn { get; set; } = string.Empty;
    public string LocalColumn { get; set; } = string.Empty;
}

public class QueryResult
{
    public bool Success { get; set; }
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public List<string> ColumnNames { get; set; } = new();
    public int TotalRowsReturned { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ExecutedQuery { get; set; }
}

public class SqlServerMcpService : ISqlServerMcpService
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerMcpService> _logger;
    private readonly int _maxRowsPerQuery;
    private readonly int _queryTimeoutSeconds;

    public SqlServerMcpService(IConfiguration configuration, ILogger<SqlServerMcpService> logger)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("SQLServerConnection")
            ?? throw new InvalidOperationException("SQLServerConnection connection string not configured");

        _maxRowsPerQuery = configuration.GetValue("SqlServerMcp:MaxRowsPerQuery", 1000);
        _queryTimeoutSeconds = configuration.GetValue("SqlServerMcp:QueryTimeoutSeconds", 30);

        _logger.LogInformation("SQL Server MCP Service initialized. MaxRows: {MaxRows}, Timeout: {Timeout}s",
            _maxRowsPerQuery, _queryTimeoutSeconds);
    }

    public async Task<List<string>> GetTableNamesAsync()
    {
        var tables = new List<string>();

        const string query = @"
            SELECT TABLE_SCHEMA + '.' + TABLE_NAME as FullName
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_SCHEMA, TABLE_NAME";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(query, connection);
        command.CommandTimeout = _queryTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        _logger.LogInformation("Retrieved {Count} tables from database", tables.Count);
        return tables;
    }

    public async Task<TableSchema> GetTableSchemaAsync(string tableName)
    {
        // Clean and parse schema.table format
        // Handle brackets: [dbo].[table_name] -> dbo.table_name
        var cleanedName = tableName
            .Replace("[", "")
            .Replace("]", "")
            .Trim();

        var parts = cleanedName.Split('.');
        string schemaName = "dbo";
        string tableNameOnly = cleanedName;

        if (parts.Length == 2)
        {
            schemaName = parts[0];
            tableNameOnly = parts[1];
        }
        else if (parts.Length > 2)
        {
            // Handle database.schema.table format
            schemaName = parts[^2]; // second to last
            tableNameOnly = parts[^1]; // last
        }

        _logger.LogInformation("GetTableSchemaAsync: Input='{Input}', Parsed Schema='{Schema}', Table='{Table}'",
            tableName, schemaName, tableNameOnly);

        var schema = new TableSchema
        {
            TableName = tableNameOnly,
            SchemaName = schemaName
        };

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Get column information
        const string columnQuery = @"
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.CHARACTER_MAXIMUM_LENGTH,
                CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END as IsPrimaryKey
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                    ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                    AND tc.TABLE_SCHEMA = ku.TABLE_SCHEMA
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) pk ON c.TABLE_SCHEMA = pk.TABLE_SCHEMA
                AND c.TABLE_NAME = pk.TABLE_NAME
                AND c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = @Schema AND c.TABLE_NAME = @TableName
            ORDER BY c.ORDINAL_POSITION";

        await using (var command = new SqlCommand(columnQuery, connection))
        {
            command.CommandTimeout = _queryTimeoutSeconds;
            command.Parameters.AddWithValue("@Schema", schemaName);
            command.Parameters.AddWithValue("@TableName", tableNameOnly);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var column = new ColumnInfo
                {
                    Name = reader.GetString(0),
                    DataType = reader.GetString(1),
                    IsNullable = reader.GetString(2) == "YES",
                    MaxLength = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    IsPrimaryKey = reader.GetInt32(4) == 1
                };
                schema.Columns.Add(column);

                if (column.IsPrimaryKey)
                {
                    schema.PrimaryKeys.Add(column.Name);
                }
            }
        }

        // If no columns found, check if table exists and find similar names
        if (schema.Columns.Count == 0)
        {
            _logger.LogWarning("No columns found for table {Schema}.{Table}. Checking for similar table names...",
                schemaName, tableNameOnly);

            // Find similar table names to help diagnose the issue
            const string similarTablesQuery = @"
                SELECT TABLE_SCHEMA + '.' + TABLE_NAME as FullName
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_TYPE = 'BASE TABLE'
                  AND (TABLE_NAME LIKE @Pattern1 OR TABLE_NAME LIKE @Pattern2 OR TABLE_NAME LIKE @Pattern3)
                ORDER BY TABLE_NAME";

            await using var similarCmd = new SqlCommand(similarTablesQuery, connection);
            similarCmd.CommandTimeout = _queryTimeoutSeconds;
            similarCmd.Parameters.AddWithValue("@Pattern1", $"%{tableNameOnly}%");
            similarCmd.Parameters.AddWithValue("@Pattern2", $"{tableNameOnly.TrimEnd('s')}%"); // Remove trailing 's'
            similarCmd.Parameters.AddWithValue("@Pattern3", $"{tableNameOnly}s%"); // Add trailing 's'

            var similarTables = new List<string>();
            await using var similarReader = await similarCmd.ExecuteReaderAsync();
            while (await similarReader.ReadAsync())
            {
                similarTables.Add(similarReader.GetString(0));
            }

            if (similarTables.Any())
            {
                _logger.LogWarning("Similar tables found: {Tables}", string.Join(", ", similarTables));
                schema.SimilarTableNames = similarTables;
            }
        }

        // Get foreign key information (this table references other tables)
        const string fkQuery = @"
            SELECT
                COL_NAME(fc.parent_object_id, fc.parent_column_id) as ColumnName,
                OBJECT_SCHEMA_NAME(fc.referenced_object_id) + '.' + OBJECT_NAME(fc.referenced_object_id) as ReferencedTable,
                COL_NAME(fc.referenced_object_id, fc.referenced_column_id) as ReferencedColumn
            FROM sys.foreign_key_columns fc
            JOIN sys.tables t ON fc.parent_object_id = t.object_id
            WHERE OBJECT_SCHEMA_NAME(t.object_id) = @Schema AND t.name = @TableName";

        await using (var command = new SqlCommand(fkQuery, connection))
        {
            command.CommandTimeout = _queryTimeoutSeconds;
            command.Parameters.AddWithValue("@Schema", schemaName);
            command.Parameters.AddWithValue("@TableName", tableNameOnly);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schema.ForeignKeys.Add(new ForeignKeyInfo
                {
                    ColumnName = reader.GetString(0),
                    ReferencedTable = reader.GetString(1),
                    ReferencedColumn = reader.GetString(2)
                });
            }
        }

        // Get reverse relationships (other tables that reference this table)
        const string referencedByQuery = @"
            SELECT
                OBJECT_SCHEMA_NAME(fc.parent_object_id) + '.' + OBJECT_NAME(fc.parent_object_id) as ReferencingTable,
                COL_NAME(fc.parent_object_id, fc.parent_column_id) as ReferencingColumn,
                COL_NAME(fc.referenced_object_id, fc.referenced_column_id) as LocalColumn
            FROM sys.foreign_key_columns fc
            JOIN sys.tables t ON fc.referenced_object_id = t.object_id
            WHERE OBJECT_SCHEMA_NAME(t.object_id) = @Schema AND t.name = @TableName";

        await using (var command = new SqlCommand(referencedByQuery, connection))
        {
            command.CommandTimeout = _queryTimeoutSeconds;
            command.Parameters.AddWithValue("@Schema", schemaName);
            command.Parameters.AddWithValue("@TableName", tableNameOnly);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schema.ReferencedBy.Add(new ReferencedByInfo
                {
                    ReferencingTable = reader.GetString(0),
                    ReferencingColumn = reader.GetString(1),
                    LocalColumn = reader.GetString(2)
                });
            }
        }

        _logger.LogInformation("Retrieved schema for {Table}: {ColumnCount} columns, {PKCount} PKs, {FKCount} FKs, {RefByCount} referenced by",
            tableName, schema.Columns.Count, schema.PrimaryKeys.Count, schema.ForeignKeys.Count, schema.ReferencedBy.Count);

        return schema;
    }

    public async Task<QueryResult> ExecuteQueryAsync(string query, int maxRows = 100)
    {
        var result = new QueryResult();

        // Security: Validate query is read-only
        var validationError = ValidateReadOnlyQuery(query);
        if (validationError != null)
        {
            result.Success = false;
            result.ErrorMessage = validationError;
            _logger.LogWarning("Query validation failed: {Error}. Query: {Query}", validationError, query);
            return result;
        }

        // Enforce row limit
        var effectiveMaxRows = Math.Min(maxRows, _maxRowsPerQuery);
        var limitedQuery = EnsureRowLimit(query, effectiveMaxRows);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new SqlCommand(limitedQuery, connection);
            command.CommandTimeout = _queryTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync();

            // Get column names
            for (int i = 0; i < reader.FieldCount; i++)
            {
                result.ColumnNames.Add(reader.GetName(i));
            }

            // Read rows
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    // Convert to JSON-serializable types
                    row[result.ColumnNames[i]] = ConvertToSerializable(value);
                }
                result.Rows.Add(row);
            }

            result.Success = true;
            result.TotalRowsReturned = result.Rows.Count;
            result.ExecutedQuery = limitedQuery;

            _logger.LogInformation("Query executed successfully. Returned {RowCount} rows", result.TotalRowsReturned);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Query execution error: {ex.Message}";
            _logger.LogError(ex, "Error executing query: {Query}", limitedQuery);
        }

        return result;
    }

    public async Task<List<Dictionary<string, object?>>> GetSampleDataAsync(string tableName, int sampleSize = 5)
    {
        // Sanitize table name to prevent injection
        var sanitizedTableName = SanitizeTableName(tableName);
        if (sanitizedTableName == null)
        {
            _logger.LogWarning("Invalid table name: {TableName}", tableName);
            return new List<Dictionary<string, object?>>();
        }

        var query = $"SELECT TOP {Math.Min(sampleSize, 10)} * FROM {sanitizedTableName}";
        var result = await ExecuteQueryAsync(query, sampleSize);

        return result.Success ? result.Rows : new List<Dictionary<string, object?>>();
    }

    private string? ValidateReadOnlyQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Query cannot be empty";
        }

        var normalizedQuery = query.Trim().ToUpperInvariant();

        // Must start with SELECT or WITH (for CTEs)
        if (!normalizedQuery.StartsWith("SELECT") && !normalizedQuery.StartsWith("WITH"))
        {
            return "Only SELECT queries are allowed";
        }

        // Check for dangerous keywords
        var dangerousPatterns = new[]
        {
            @"\bINSERT\b", @"\bUPDATE\b", @"\bDELETE\b", @"\bDROP\b",
            @"\bTRUNCATE\b", @"\bALTER\b", @"\bCREATE\b", @"\bEXEC\b",
            @"\bEXECUTE\b", @"\bGRANT\b", @"\bREVOKE\b", @"\bDENY\b",
            @"\bBACKUP\b", @"\bRESTORE\b", @"\bSHUTDOWN\b", @"\bKILL\b",
            @"\bxp_", @"\bsp_"
        };

        foreach (var pattern in dangerousPatterns)
        {
            if (Regex.IsMatch(normalizedQuery, pattern, RegexOptions.IgnoreCase))
            {
                return $"Query contains prohibited keyword matching pattern: {pattern}";
            }
        }

        // Check for semicolons (potential multiple statements)
        if (query.Contains(';'))
        {
            // Allow semicolon only at the very end
            var trimmed = query.TrimEnd();
            if (trimmed.IndexOf(';') != trimmed.Length - 1)
            {
                return "Multiple SQL statements are not allowed";
            }
        }

        return null; // Query is valid
    }

    private string EnsureRowLimit(string query, int maxRows)
    {
        var normalizedQuery = query.Trim().ToUpperInvariant();

        // If query already has TOP, don't modify it but ensure it's not too high
        if (Regex.IsMatch(normalizedQuery, @"\bSELECT\s+TOP\s+\d+", RegexOptions.IgnoreCase))
        {
            // Extract the TOP value and ensure it's within limits
            var match = Regex.Match(query, @"\bTOP\s+(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var existingTop))
            {
                if (existingTop > maxRows)
                {
                    // Replace with our limit
                    return Regex.Replace(query, @"\bTOP\s+\d+", $"TOP {maxRows}", RegexOptions.IgnoreCase);
                }
            }
            return query;
        }

        // Add TOP clause after SELECT
        return Regex.Replace(query, @"\bSELECT\b", $"SELECT TOP {maxRows}", RegexOptions.IgnoreCase);
    }

    private string? SanitizeTableName(string tableName)
    {
        // Only allow alphanumeric, underscore, dot (for schema.table), and brackets
        if (!Regex.IsMatch(tableName, @"^[\w\.\[\]]+$"))
        {
            return null;
        }

        // Ensure proper bracketing
        var parts = tableName.Split('.');
        var sanitizedParts = parts.Select(p =>
        {
            var trimmed = p.Trim('[', ']');
            return $"[{trimmed}]";
        });

        return string.Join(".", sanitizedParts);
    }

    private object? ConvertToSerializable(object? value)
    {
        if (value == null) return null;

        return value switch
        {
            DateTime dt => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            TimeSpan ts => ts.ToString(),
            byte[] bytes => Convert.ToBase64String(bytes),
            Guid g => g.ToString(),
            decimal d => d,
            _ => value
        };
    }
}
