using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RiskWeb.Models;

[Table("Regions", Schema = "dbo")]
public class Region
{
    [Key]
    [Column("RegionID")]
    public int RegionID { get; set; }

    [StringLength(50)]
    [Column("RegionDescription")]
    public string RegionDescription { get; set; } = string.Empty;
}
