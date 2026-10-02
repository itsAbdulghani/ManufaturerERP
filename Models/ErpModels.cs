using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace ManufacturerERP.Models;

public class ApplicationUser : IdentityUser
{
    [Required, StringLength(150)] public string FullName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}
public abstract class MasterRecord { public int Id { get; set; } public bool IsActive { get; set; } = true; public DateTime CreatedAt { get; set; } = DateTime.UtcNow; public DateTime? UpdatedAt { get; set; } }
public class Unit : MasterRecord { [Required, StringLength(100)] public string Name { get; set; } = ""; [Required, StringLength(20)] public string ShortCode { get; set; } = ""; [StringLength(300)] public string? Description { get; set; } }
public class Warehouse : MasterRecord { [Required, StringLength(120)] public string Name { get; set; } = ""; [Required, StringLength(30)] public string Code { get; set; } = ""; public string? Address { get; set; } public string? City { get; set; } public string? Description { get; set; } }
public enum ItemType { RawMaterial, FinishedGood, Consumable, Other }
public class Product : MasterRecord { [Required, StringLength(50)] public string Code { get; set; } = ""; [Required, StringLength(180)] public string Name { get; set; } = ""; public string? Description { get; set; } public ItemType ItemType { get; set; } [Display(Name="Unit")] public int UnitId { get; set; } public Unit? Unit { get; set; } [Range(0, 999999999)] public decimal PurchasePrice { get; set; } [Range(0, 999999999)] public decimal SalePrice { get; set; } [Range(0, 999999999)] public decimal MinimumStockLevel { get; set; } }
public abstract class Party : MasterRecord { [Required, StringLength(30)] public string Code { get; set; } = ""; [Required, StringLength(180)] public string Name { get; set; } = ""; public string? ContactPerson { get; set; } [Phone] public string? Phone { get; set; } [EmailAddress] public string? Email { get; set; } public string? Address { get; set; } public string? City { get; set; } public string? Province { get; set; } public string? NTN { get; set; } public string? STRN { get; set; } public int PaymentTerms { get; set; } public string? Notes { get; set; } }
public class Customer : Party { [Range(0, 999999999)] public decimal CreditLimit { get; set; } }
public class Supplier : Party { }
public class CompanySetting { public int Id { get; set; } [Required] public string CompanyName { get; set; } = ""; public string? LegalName { get; set; } public string? NTN { get; set; } public string? STRN { get; set; } public string? Address { get; set; } public string? City { get; set; } public string? Province { get; set; } public string? Phone { get; set; } [EmailAddress] public string? Email { get; set; } public string? Website { get; set; } public string Currency { get; set; } = "PKR"; public string CurrencySymbol { get; set; } = "Rs."; public string InvoicePrefix { get; set; } = "INV"; public string PurchasePrefix { get; set; } = "PUR"; public string? LogoPath { get; set; } public DateOnly? FinancialYearStart { get; set; } public DateOnly? FinancialYearEnd { get; set; } public DateTime UpdatedAt { get; set; } = DateTime.UtcNow; }
public class AuditLog { public long Id { get; set; } public string? UserId { get; set; } public string Action { get; set; } = ""; public string EntityName { get; set; } = ""; public string? EntityId { get; set; } public string? Description { get; set; } public DateTime Timestamp { get; set; } = DateTime.UtcNow; }
