using ManufacturerERP.Data;
using ManufacturerERP.Models;
using System.Security.Claims;

namespace ManufacturerERP.Services;
public interface IAuditService { Task LogAsync(ClaimsPrincipal user, string action, string entity, object? id, string description); }
public class AuditService(ApplicationDbContext db) : IAuditService
{
    public async Task LogAsync(ClaimsPrincipal user, string action, string entity, object? id, string description)
    {
        db.AuditLogs.Add(new AuditLog { UserId = user.FindFirstValue(ClaimTypes.NameIdentifier), Action = action, EntityName = entity, EntityId = id?.ToString(), Description = description });
        await db.SaveChangesAsync();
    }
}
