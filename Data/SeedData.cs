using ManufacturerERP.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ManufacturerERP.Data;
public static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Admin", "Manager", "Storekeeper", "Sales", "Purchase", "Accountant", "Production" })
            if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var email = "admin@manufacturererp.pk";
        if (await users.FindByEmailAsync(email) is null)
        {
            var user = new ApplicationUser { UserName = email, Email = email, FullName = "System Administrator", EmailConfirmed = true };
            await users.CreateAsync(user, "Admin@123");
            await users.AddToRoleAsync(user, "Admin");
        }
    }
}
