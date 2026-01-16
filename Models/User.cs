using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RiskWeb.Models;

[Table("Users", Schema = "dbo")]
public class User
{
    [Key]
    public int UserId { get; set; }

    [Required]
    [MaxLength(256)]
    public string WindowsUsername { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? DisplayName { get; set; }

    [MaxLength(256)]
    public string? Email { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.Now;

    public DateTime? ModifiedDate { get; set; }

    public virtual ICollection<UserInRole> UserInRoles { get; set; } = new List<UserInRole>();
}
