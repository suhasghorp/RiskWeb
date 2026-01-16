using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RiskWeb.Models;

[Table("Roles", Schema = "dbo")]
public class Role
{
    [Key]
    public int RoleId { get; set; }

    [Required]
    [MaxLength(100)]
    public string RoleName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;

    public virtual ICollection<UserInRole> UserInRoles { get; set; } = new List<UserInRole>();
}
