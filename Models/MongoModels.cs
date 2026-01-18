using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RiskWeb.Models;

[BsonIgnoreExtraElements]
public class Movie
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("title")]
    public string? Title { get; set; }

    [BsonElement("year")]
    public BsonValue? Year { get; set; }  // Can be int or string in the database

    [BsonElement("plot")]
    public string? Plot { get; set; }

    [BsonElement("fullplot")]
    public string? FullPlot { get; set; }

    [BsonElement("genres")]
    public List<string>? Genres { get; set; }

    [BsonElement("runtime")]
    public int? Runtime { get; set; }

    [BsonElement("cast")]
    public List<string>? Cast { get; set; }

    [BsonElement("directors")]
    public List<string>? Directors { get; set; }

    [BsonElement("countries")]
    public List<string>? Countries { get; set; }

    [BsonElement("rated")]
    public string? Rated { get; set; }

    [BsonElement("type")]
    public string? Type { get; set; }

    [BsonElement("imdb")]
    public ImdbInfo? Imdb { get; set; }

    [BsonElement("tomatoes")]
    public TomatoesInfo? Tomatoes { get; set; }

    [BsonElement("awards")]
    public AwardsInfo? Awards { get; set; }

    [BsonElement("lastupdated")]
    public string? LastUpdated { get; set; }

    [BsonElement("num_mflix_comments")]
    public int? NumComments { get; set; }

    // Helper to get year as integer
    [BsonIgnore]
    public int? YearAsInt
    {
        get
        {
            if (Year == null || Year.IsBsonNull) return null;
            if (Year.IsInt32) return Year.AsInt32;
            if (Year.IsString && int.TryParse(Year.AsString, out var yearInt)) return yearInt;
            return null;
        }
    }
}

[BsonIgnoreExtraElements]
public class ImdbInfo
{
    [BsonElement("rating")]
    public BsonValue? Rating { get; set; }

    [BsonElement("votes")]
    public BsonValue? Votes { get; set; }  // Can be int or empty string in the database

    [BsonElement("id")]
    public BsonValue? Id { get; set; }  // Can be int or empty string in the database

    [BsonIgnore]
    public double? RatingAsDouble
    {
        get
        {
            if (Rating == null || Rating.IsBsonNull) return null;
            if (Rating.IsDouble) return Rating.AsDouble;
            if (Rating.IsInt32) return Rating.AsInt32;
            if (Rating.IsString && double.TryParse(Rating.AsString, out var ratingDbl)) return ratingDbl;
            return null;
        }
    }

    [BsonIgnore]
    public int? VotesAsInt
    {
        get
        {
            if (Votes == null || Votes.IsBsonNull) return null;
            if (Votes.IsInt32) return Votes.AsInt32;
            if (Votes.IsInt64) return (int)Votes.AsInt64;
            if (Votes.IsString && int.TryParse(Votes.AsString, out var votesInt)) return votesInt;
            return null;
        }
    }
}

[BsonIgnoreExtraElements]
public class TomatoesInfo
{
    [BsonElement("viewer")]
    public TomatoesViewer? Viewer { get; set; }

    [BsonElement("lastUpdated")]
    public DateTime? LastUpdated { get; set; }
}

[BsonIgnoreExtraElements]
public class TomatoesViewer
{
    [BsonElement("rating")]
    public double? Rating { get; set; }

    [BsonElement("numReviews")]
    public int? NumReviews { get; set; }

    [BsonElement("meter")]
    public int? Meter { get; set; }
}

[BsonIgnoreExtraElements]
public class AwardsInfo
{
    [BsonElement("wins")]
    public int? Wins { get; set; }

    [BsonElement("nominations")]
    public int? Nominations { get; set; }

    [BsonElement("text")]
    public string? Text { get; set; }
}

[BsonIgnoreExtraElements]
public class Theater
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("theaterId")]
    public int? TheaterId { get; set; }

    [BsonElement("location")]
    public TheaterLocation? Location { get; set; }
}

[BsonIgnoreExtraElements]
public class TheaterLocation
{
    [BsonElement("address")]
    public TheaterAddress? Address { get; set; }

    [BsonElement("geo")]
    public GeoPoint? Geo { get; set; }
}

[BsonIgnoreExtraElements]
public class TheaterAddress
{
    [BsonElement("street1")]
    public string? Street1 { get; set; }

    [BsonElement("city")]
    public string? City { get; set; }

    [BsonElement("state")]
    public string? State { get; set; }

    [BsonElement("zipcode")]
    public string? Zipcode { get; set; }
}

[BsonIgnoreExtraElements]
public class GeoPoint
{
    [BsonElement("type")]
    public string? Type { get; set; }

    [BsonElement("coordinates")]
    public double[]? Coordinates { get; set; }
}

// Result class for aggregation
public class YearCount
{
    public int Year { get; set; }
    public int MovieCount { get; set; }
}
