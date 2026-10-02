using System.ComponentModel.DataAnnotations;

namespace ManufacturerERP.Models;

public class UserFormViewModel
{
    public string? Id { get; set; }
    [Required, StringLength(150)] public string FullName { get; set; } = "";
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Phone] public string? PhoneNumber { get; set; }
    [Required] public string RoleName { get; set; } = "";
    public string? Password { get; set; }
}

public class UserListItemViewModel
{
    public ApplicationUser User { get; set; } = new();
    public string Roles { get; set; } = "";
}
