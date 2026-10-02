using ManufacturerERP.Data;
using ManufacturerERP.Models;
using ManufacturerERP.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace ManufacturerERP.Controllers;
[Authorize(Roles="Admin")]
public class CompanyController(ApplicationDbContext db, IAuditService audit) : Controller
{
    public async Task<IActionResult> Edit() => View(await db.CompanySettings.FirstOrDefaultAsync() ?? new CompanySetting());
    [HttpPost, ValidateAntiForgeryToken] public async Task<IActionResult> Edit(CompanySetting model) { if(!ModelState.IsValid)return View(model); var existing=await db.CompanySettings.FirstOrDefaultAsync(); if(existing is null) db.CompanySettings.Add(model); else { model.Id=existing.Id; db.Entry(existing).CurrentValues.SetValues(model); } await db.SaveChangesAsync(); await audit.LogAsync(User,"Update","CompanySetting",model.Id,"Updated company configuration"); TempData["Success"]="Company settings saved.";return RedirectToAction(nameof(Edit)); }
}
