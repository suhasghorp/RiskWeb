using ClosedXML.Excel;
using RiskWeb.Models;

namespace RiskWeb.Services.Chat;

public interface IExcelExportService
{
    Task<ExportResult> ExportMoviesToExcelAsync(List<Movie> movies, string queryDescription);
    Task<ExportResult> ExportYearCountsToExcelAsync(List<YearCount> yearCounts, string queryDescription);
    Task<ExportResult> ExportSqlDataToExcelAsync(List<string> columns, List<Dictionary<string, object?>> rows, string queryDescription, string? sheetName = null);
    byte[]? GetExportedFile(string fileId);
    void CleanupOldFiles();
}

public class ExportResult
{
    public bool Success { get; set; }
    public string? FileId { get; set; }
    public string? FileName { get; set; }
    public string? ErrorMessage { get; set; }
    public int RecordCount { get; set; }
}

public class ExcelExportService : IExcelExportService
{
    private readonly ILogger<ExcelExportService> _logger;
    private readonly Dictionary<string, (byte[] Data, DateTime Created, string FileName)> _exportedFiles = new();
    private readonly object _lock = new();
    private readonly TimeSpan _fileRetention = TimeSpan.FromMinutes(30);

    public ExcelExportService(ILogger<ExcelExportService> logger)
    {
        _logger = logger;
    }

    public Task<ExportResult> ExportMoviesToExcelAsync(List<Movie> movies, string queryDescription)
    {
        try
        {
            _logger.LogInformation("Exporting {Count} movies to Excel", movies.Count);

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Movies");

            // Add header row
            worksheet.Cell(1, 1).Value = "Title";
            worksheet.Cell(1, 2).Value = "Year";
            worksheet.Cell(1, 3).Value = "Genres";
            worksheet.Cell(1, 4).Value = "Directors";
            worksheet.Cell(1, 5).Value = "Cast";
            worksheet.Cell(1, 6).Value = "Runtime (min)";
            worksheet.Cell(1, 7).Value = "Rated";
            worksheet.Cell(1, 8).Value = "IMDB Rating";
            worksheet.Cell(1, 9).Value = "IMDB Votes";
            worksheet.Cell(1, 10).Value = "Plot";

            // Style header
            var headerRange = worksheet.Range(1, 1, 1, 10);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;
            headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

            // Add data rows
            for (int i = 0; i < movies.Count; i++)
            {
                var movie = movies[i];
                var row = i + 2;

                worksheet.Cell(row, 1).Value = movie.Title ?? "";
                worksheet.Cell(row, 2).Value = movie.YearAsInt ?? 0;
                worksheet.Cell(row, 3).Value = movie.Genres != null ? string.Join(", ", movie.Genres) : "";
                worksheet.Cell(row, 4).Value = movie.Directors != null ? string.Join(", ", movie.Directors) : "";
                worksheet.Cell(row, 5).Value = movie.Cast != null ? string.Join(", ", movie.Cast.Take(5)) : "";
                worksheet.Cell(row, 6).Value = movie.Runtime ?? 0;
                worksheet.Cell(row, 7).Value = movie.Rated ?? "";
                worksheet.Cell(row, 8).Value = movie.Imdb?.RatingAsDouble ?? 0;
                worksheet.Cell(row, 9).Value = movie.Imdb?.VotesAsInt ?? 0;
                worksheet.Cell(row, 10).Value = movie.Plot ?? "";
            }

            // Auto-fit columns
            worksheet.Columns().AdjustToContents();

            // Add query info sheet
            var infoSheet = workbook.Worksheets.Add("Query Info");
            infoSheet.Cell(1, 1).Value = "Query:";
            infoSheet.Cell(1, 2).Value = queryDescription;
            infoSheet.Cell(2, 1).Value = "Exported:";
            infoSheet.Cell(2, 2).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            infoSheet.Cell(3, 1).Value = "Record Count:";
            infoSheet.Cell(3, 2).Value = movies.Count;
            infoSheet.Column(1).Style.Font.Bold = true;
            infoSheet.Columns().AdjustToContents();

            // Save to memory
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var fileData = stream.ToArray();

            var fileId = Guid.NewGuid().ToString("N")[..12];
            var fileName = $"Movies_Export_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";

            lock (_lock)
            {
                _exportedFiles[fileId] = (fileData, DateTime.Now, fileName);
            }

            _logger.LogInformation("Excel export created: {FileId}, {FileName}, {Size} bytes",
                fileId, fileName, fileData.Length);

            return Task.FromResult(new ExportResult
            {
                Success = true,
                FileId = fileId,
                FileName = fileName,
                RecordCount = movies.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting movies to Excel");
            return Task.FromResult(new ExportResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public Task<ExportResult> ExportYearCountsToExcelAsync(List<YearCount> yearCounts, string queryDescription)
    {
        try
        {
            _logger.LogInformation("Exporting {Count} year counts to Excel", yearCounts.Count);

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Movie Counts by Year");

            // Add header row
            worksheet.Cell(1, 1).Value = "Year";
            worksheet.Cell(1, 2).Value = "Movie Count";

            // Style header
            var headerRange = worksheet.Range(1, 1, 1, 2);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightGreen;
            headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

            // Add data rows
            for (int i = 0; i < yearCounts.Count; i++)
            {
                var yc = yearCounts[i];
                var row = i + 2;

                worksheet.Cell(row, 1).Value = yc.Year;
                worksheet.Cell(row, 2).Value = yc.MovieCount;
            }

            // Auto-fit columns
            worksheet.Columns().AdjustToContents();

            // Add total row
            var totalRow = yearCounts.Count + 2;
            worksheet.Cell(totalRow, 1).Value = "Total";
            worksheet.Cell(totalRow, 1).Style.Font.Bold = true;
            worksheet.Cell(totalRow, 2).FormulaA1 = $"=SUM(B2:B{totalRow - 1})";
            worksheet.Cell(totalRow, 2).Style.Font.Bold = true;

            // Add query info sheet
            var infoSheet = workbook.Worksheets.Add("Query Info");
            infoSheet.Cell(1, 1).Value = "Query:";
            infoSheet.Cell(1, 2).Value = queryDescription;
            infoSheet.Cell(2, 1).Value = "Exported:";
            infoSheet.Cell(2, 2).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            infoSheet.Cell(3, 1).Value = "Years Count:";
            infoSheet.Cell(3, 2).Value = yearCounts.Count;
            infoSheet.Column(1).Style.Font.Bold = true;
            infoSheet.Columns().AdjustToContents();

            // Save to memory
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var fileData = stream.ToArray();

            var fileId = Guid.NewGuid().ToString("N")[..12];
            var fileName = $"MovieCounts_Export_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";

            lock (_lock)
            {
                _exportedFiles[fileId] = (fileData, DateTime.Now, fileName);
            }

            _logger.LogInformation("Excel export created: {FileId}, {FileName}, {Size} bytes",
                fileId, fileName, fileData.Length);

            return Task.FromResult(new ExportResult
            {
                Success = true,
                FileId = fileId,
                FileName = fileName,
                RecordCount = yearCounts.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting year counts to Excel");
            return Task.FromResult(new ExportResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public Task<ExportResult> ExportSqlDataToExcelAsync(List<string> columns, List<Dictionary<string, object?>> rows, string queryDescription, string? sheetName = null)
    {
        try
        {
            _logger.LogInformation("Exporting {Count} rows with {ColCount} columns to Excel", rows.Count, columns.Count);

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add(sheetName ?? "Query Results");

            // Add header row
            for (int col = 0; col < columns.Count; col++)
            {
                worksheet.Cell(1, col + 1).Value = columns[col];
            }

            // Style header
            var headerRange = worksheet.Range(1, 1, 1, columns.Count);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;
            headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

            // Add data rows
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var excelRow = rowIndex + 2;

                for (int colIndex = 0; colIndex < columns.Count; colIndex++)
                {
                    var colName = columns[colIndex];
                    var cell = worksheet.Cell(excelRow, colIndex + 1);

                    if (row.TryGetValue(colName, out var value) && value != null)
                    {
                        // Handle different types appropriately
                        switch (value)
                        {
                            case int intVal:
                                cell.Value = intVal;
                                break;
                            case long longVal:
                                cell.Value = longVal;
                                break;
                            case decimal decVal:
                                cell.Value = decVal;
                                break;
                            case double dblVal:
                                cell.Value = dblVal;
                                break;
                            case float fltVal:
                                cell.Value = fltVal;
                                break;
                            case bool boolVal:
                                cell.Value = boolVal;
                                break;
                            case DateTime dtVal:
                                cell.Value = dtVal;
                                cell.Style.NumberFormat.Format = "yyyy-MM-dd HH:mm:ss";
                                break;
                            default:
                                cell.Value = value.ToString();
                                break;
                        }
                    }
                }
            }

            // Auto-fit columns (with max width to prevent very wide columns)
            foreach (var col in worksheet.ColumnsUsed())
            {
                col.AdjustToContents();
                if (col.Width > 50)
                {
                    col.Width = 50;
                }
            }

            // Add query info sheet
            var infoSheet = workbook.Worksheets.Add("Query Info");
            infoSheet.Cell(1, 1).Value = "Query:";
            infoSheet.Cell(1, 2).Value = queryDescription;
            infoSheet.Cell(2, 1).Value = "Exported:";
            infoSheet.Cell(2, 2).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            infoSheet.Cell(3, 1).Value = "Record Count:";
            infoSheet.Cell(3, 2).Value = rows.Count;
            infoSheet.Cell(4, 1).Value = "Columns:";
            infoSheet.Cell(4, 2).Value = columns.Count;
            infoSheet.Column(1).Style.Font.Bold = true;
            infoSheet.Columns().AdjustToContents();

            // Save to memory
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var fileData = stream.ToArray();

            var fileId = Guid.NewGuid().ToString("N")[..12];
            var fileName = $"SqlExport_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";

            lock (_lock)
            {
                _exportedFiles[fileId] = (fileData, DateTime.Now, fileName);
            }

            _logger.LogInformation("SQL Excel export created: {FileId}, {FileName}, {Size} bytes",
                fileId, fileName, fileData.Length);

            return Task.FromResult(new ExportResult
            {
                Success = true,
                FileId = fileId,
                FileName = fileName,
                RecordCount = rows.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting SQL data to Excel");
            return Task.FromResult(new ExportResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public byte[]? GetExportedFile(string fileId)
    {
        lock (_lock)
        {
            if (_exportedFiles.TryGetValue(fileId, out var file))
            {
                return file.Data;
            }
            return null;
        }
    }

    public void CleanupOldFiles()
    {
        lock (_lock)
        {
            var expiredFiles = _exportedFiles
                .Where(kvp => DateTime.Now - kvp.Value.Created > _fileRetention)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var fileId in expiredFiles)
            {
                _exportedFiles.Remove(fileId);
                _logger.LogInformation("Cleaned up expired export file: {FileId}", fileId);
            }
        }
    }
}
