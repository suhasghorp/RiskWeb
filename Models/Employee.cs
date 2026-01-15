using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RiskWeb.Models;

[Table("Employees", Schema = "dbo")]
public class Employee
{
    [Key]
    [Column("EmployeeID")]
    public int EmployeeID { get; set; }

    [Required]
    [StringLength(20)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [StringLength(10)]
    public string FirstName { get; set; } = string.Empty;

    [StringLength(30)]
    public string? Title { get; set; }

    [StringLength(25)]
    public string? TitleOfCourtesy { get; set; }

    public DateTime? BirthDate { get; set; }

    public DateTime? HireDate { get; set; }

    [StringLength(60)]
    public string? Address { get; set; }

    [StringLength(15)]
    public string? City { get; set; }

    [StringLength(15)]
    public string? Region { get; set; }

    [StringLength(10)]
    public string? PostalCode { get; set; }

    [StringLength(15)]
    public string? Country { get; set; }

    [StringLength(24)]
    public string? HomePhone { get; set; }

    [StringLength(4)]
    public string? Extension { get; set; }
}
