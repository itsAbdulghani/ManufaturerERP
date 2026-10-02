using ManufacturerERP.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ManufacturerERP.Models;
using ManufacturerERP.Services;
namespace ManufacturerERP.Controllers;
[Authorize(Roles="Admin")]
public class AdministrationController(ApplicationDbContext db, UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles, IAuditService audit) : Controller
{
    public async Task<IActionResult> Users()
    {
        var rows=new List<UserListItemViewModel>();
        foreach(var user in await users.Users.OrderBy(x=>x.FullName).ToListAsync())rows.Add(new UserListItemViewModel{User=user,Roles=string.Join(", ",await users.GetRolesAsync(user))});
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> CreateUser(){await LoadRolesAsync();return View("UserForm",new UserFormViewModel());}

    [HttpPost,ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUser(UserFormViewModel model)
    {
        if(string.IsNullOrWhiteSpace(model.Password)||model.Password.Length<6)ModelState.AddModelError(nameof(model.Password),"Password must contain at least 6 characters.");
        if(!await roles.RoleExistsAsync(model.RoleName))ModelState.AddModelError(nameof(model.RoleName),"Select an existing role.");
        if(!ModelState.IsValid){await LoadRolesAsync();return View("UserForm",model);}
        var user=new ApplicationUser{UserName=model.Email,Email=model.Email,FullName=model.FullName,PhoneNumber=model.PhoneNumber,EmailConfirmed=true,IsActive=true};
        var result=await users.CreateAsync(user,model.Password!);
        if(!result.Succeeded){AddIdentityErrors(result);await LoadRolesAsync();return View("UserForm",model);}
        result=await users.AddToRoleAsync(user,model.RoleName);
        if(!result.Succeeded){await users.DeleteAsync(user);AddIdentityErrors(result);await LoadRolesAsync();return View("UserForm",model);}
        await audit.LogAsync(User,"Create","ApplicationUser",user.Id,$"Created user {user.Email} with role {model.RoleName}");
        TempData["Success"]=$"User {user.Email} created.";
        return RedirectToAction(nameof(Users));
    }

    [HttpGet]
    public async Task<IActionResult> EditUser(string id)
    {
        var user=await users.FindByIdAsync(id);if(user is null)return NotFound();
        var assigned=await users.GetRolesAsync(user);await LoadRolesAsync();
        return View("UserForm",new UserFormViewModel{Id=user.Id,FullName=user.FullName,Email=user.Email??"",PhoneNumber=user.PhoneNumber,RoleName=assigned.FirstOrDefault()??""});
    }

    [HttpPost,ValidateAntiForgeryToken]
    public async Task<IActionResult> EditUser(string id,UserFormViewModel model)
    {
        var user=await users.FindByIdAsync(id);if(user is null)return NotFound();
        if(!await roles.RoleExistsAsync(model.RoleName))ModelState.AddModelError(nameof(model.RoleName),"Select an existing role.");
        if(!string.IsNullOrWhiteSpace(model.Password)&&model.Password.Length<6)ModelState.AddModelError(nameof(model.Password),"Password must contain at least 6 characters.");
        if(!ModelState.IsValid){model.Id=id;await LoadRolesAsync();return View("UserForm",model);}
        user.FullName=model.FullName;
        var result=await users.SetEmailAsync(user,model.Email);
        if(result.Succeeded)result=await users.SetUserNameAsync(user,model.Email);
        if(result.Succeeded)result=await users.SetPhoneNumberAsync(user,model.PhoneNumber);
        if(result.Succeeded)result=await users.UpdateAsync(user);
        if(result.Succeeded&&!string.IsNullOrWhiteSpace(model.Password)){var token=await users.GeneratePasswordResetTokenAsync(user);result=await users.ResetPasswordAsync(user,token,model.Password);}
        if(result.Succeeded)
        {
            var current=await users.GetRolesAsync(user);
            if(current.Count>0)result=await users.RemoveFromRolesAsync(user,current);
            if(result.Succeeded)result=await users.AddToRoleAsync(user,model.RoleName);
        }
        if(!result.Succeeded){AddIdentityErrors(result);model.Id=id;await LoadRolesAsync();return View("UserForm",model);}
        await audit.LogAsync(User,"Update","ApplicationUser",user.Id,$"Updated user {user.Email}; role {model.RoleName}");
        TempData["Success"]=$"User {user.Email} updated.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost,ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleUser(string id)
    {
        var user=await users.FindByIdAsync(id);if(user is null)return NotFound();
        if(user.Id==users.GetUserId(User)&&user.IsActive){TempData["Error"]="You cannot deactivate your own account.";return RedirectToAction(nameof(Users));}
        user.IsActive=!user.IsActive;
        var result=await users.UpdateAsync(user);
        if(!result.Succeeded){TempData["Error"]=string.Join(" ",result.Errors.Select(x=>x.Description));return RedirectToAction(nameof(Users));}
        await audit.LogAsync(User,user.IsActive?"Activate":"Deactivate","ApplicationUser",user.Id,$"User {user.Email} status changed");
        TempData["Success"]=$"User {user.Email} {(user.IsActive?"activated":"deactivated").ToLowerInvariant()}.";
        return RedirectToAction(nameof(Users));
    }

    public async Task<IActionResult> Audit() => View(await db.AuditLogs.OrderByDescending(x=>x.Timestamp).Take(200).ToListAsync());

    private async Task LoadRolesAsync()=>ViewBag.Roles=await roles.Roles.OrderBy(x=>x.Name).ToListAsync();
    private void AddIdentityErrors(IdentityResult result){foreach(var error in result.Errors)ModelState.AddModelError(string.Empty,error.Description);}
}
