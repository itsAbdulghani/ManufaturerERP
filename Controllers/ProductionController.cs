using ManufacturerERP.Data;
using ManufacturerERP.Models;
using ManufacturerERP.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ManufacturerERP.Controllers;

[Authorize(Roles = "Admin,Manager,Production,Storekeeper,Accountant")]
public class ProductionController(ApplicationDbContext db, IProductionService workflow, IAuditService audit) : Controller
{
    public async Task<IActionResult> Index(string? search, ProductionOrderStatus? status, int? productId, int? warehouseId, DateOnly? fromDate, DateOnly? toDate)
    {
        var query = db.ProductionOrders.Include(x => x.Product).Include(x => x.Warehouse).AsQueryable();
        if (status.HasValue) query = query.Where(x => x.Status == status);
        if (productId.HasValue) query = query.Where(x => x.ProductId == productId);
        if (warehouseId.HasValue) query = query.Where(x => x.WarehouseId == warehouseId);
        if (fromDate.HasValue) query = query.Where(x => x.ProductionDate >= fromDate);
        if (toDate.HasValue) query = query.Where(x => x.ProductionDate <= toDate);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Number.Contains(search) || x.Product!.Name.Contains(search));
        await LoadLookupsAsync();
        return View(await query.OrderByDescending(x => x.CreatedAt).ToListAsync());
    }

    [HttpGet, Authorize(Roles = "Admin,Manager,Production")]
    public async Task<IActionResult> Create()
    {
        await LoadLookupsAsync();
        return View(new ProductionOrder { ProductionDate = DateOnly.FromDateTime(DateTime.Today), Materials = [new ProductionMaterial()] });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Production")]
    public async Task<IActionResult> CreateForm(ProductionOrder order)
    {
        order.Number = await workflow.NextNumberAsync();
        ModelState.Remove(nameof(order.Number));
        if (!await ValidateOrderInputAsync(order)) return await ReturnCreateWithErrorsAsync(order);
        order.CreatedById = User.FindFirstValue(ClaimTypes.NameIdentifier);
        foreach (var material in order.Materials) { material.UnitId = (await db.Products.FindAsync(material.ProductId))!.UnitId; material.ConsumedQuantity = 0; material.TotalCost = 0; }
        db.ProductionOrders.Add(order);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "Create", "ProductionOrder", order.Id, $"Created {order.Number}");
        TempData["Success"] = $"Production order {order.Number} created.";
        return RedirectToAction(nameof(Details), new { id = order.Id });
    }

    [HttpGet, Authorize(Roles = "Admin,Manager,Production")]
    public async Task<IActionResult> Edit(int id)
    {
        var order = await db.ProductionOrders.Include(x => x.Materials).FirstOrDefaultAsync(x => x.Id == id);
        if (order is null) return NotFound();
        if (order.Status != ProductionOrderStatus.Draft) return BadRequest("Only draft production orders can be edited.");
        await LoadLookupsAsync();
        return View(order);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Production")]
    public async Task<IActionResult> EditForm(int id, ProductionOrder input)
    {
        ModelState.Remove(nameof(ProductionOrder.Number));
        var order = await db.ProductionOrders.Include(x => x.Materials).FirstOrDefaultAsync(x => x.Id == id);
        if (order is null) return NotFound();
        if (order.Status != ProductionOrderStatus.Draft) return BadRequest("Only draft production orders can be edited.");
        if (!await ValidateOrderInputAsync(input)) { input.Id = id; await LoadLookupsAsync(); return View("Edit", input); }
        order.ProductId = input.ProductId; order.WarehouseId = input.WarehouseId; order.PlannedQuantity = input.PlannedQuantity; order.ProductionDate = input.ProductionDate; order.Notes = input.Notes; order.UpdatedAt = DateTime.UtcNow;
        db.ProductionMaterials.RemoveRange(order.Materials);
        foreach (var material in input.Materials) { var product = (await db.Products.FindAsync(material.ProductId))!; order.Materials.Add(new ProductionMaterial { ProductId = material.ProductId, UnitId = product.UnitId, PlannedQuantity = material.PlannedQuantity, UnitCost = material.UnitCost, Notes = material.Notes }); }
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "Edit", "ProductionOrder", id, $"Edited {order.Number}");
        TempData["Success"] = $"Production order {order.Number} updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Production")]
    public async Task<IActionResult> Plan(int id) => await TransitionAsync(() => workflow.PlanAsync(id, User), id);

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Production")]
    public async Task<IActionResult> Start(int id) => await TransitionAsync(() => workflow.StartAsync(id, User), id);

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Production,Storekeeper")]
    public async Task<IActionResult> Consume(int id, int materialId, decimal consumedQuantity)
    {
        try { await workflow.ConsumeAsync(id, new Dictionary<int, decimal> { [materialId] = consumedQuantity }, User); TempData["Success"] = "Material consumption posted."; }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Production")]
    public async Task<IActionResult> Complete(int id, decimal actualQuantity, decimal wastageQuantity, string? notes)
    {
        try { await workflow.CompleteAsync(id, actualQuantity, wastageQuantity, notes, User); TempData["Success"] = "Production completed and finished goods posted."; }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var order = await db.ProductionOrders.Include(x => x.Product).ThenInclude(x => x!.Unit).Include(x => x.Warehouse).Include(x => x.Materials).ThenInclude(x => x.Product).ThenInclude(x => x!.Unit).FirstOrDefaultAsync(x => x.Id == id);
        if (order is null) return NotFound();
        ViewBag.Transactions = await db.InventoryTransactions.Where(x => x.ReferenceType == "ProductionOrder" && x.ReferenceId == id).Include(x => x.Product).OrderBy(x => x.Id).ToListAsync();
        return View(order);
    }

    public async Task<IActionResult> Reports(DateOnly? fromDate, DateOnly? toDate, int? productId, int? warehouseId, ProductionOrderStatus? status)
    {
        var orders = db.ProductionOrders.Include(x => x.Product).Include(x => x.Warehouse).AsQueryable();
        if (fromDate.HasValue) orders = orders.Where(x => x.ProductionDate >= fromDate);
        if (toDate.HasValue) orders = orders.Where(x => x.ProductionDate <= toDate);
        if (productId.HasValue) orders = orders.Where(x => x.ProductId == productId);
        if (warehouseId.HasValue) orders = orders.Where(x => x.WarehouseId == warehouseId);
        if (status.HasValue) orders = orders.Where(x => x.Status == status);
        var materialTransactions = db.InventoryTransactions.Where(x => x.ReferenceType == "ProductionOrder" && x.TransactionType == InventoryTransactionType.ProductionConsumption);
        var outputTransactions = db.InventoryTransactions.Where(x => x.ReferenceType == "ProductionOrder" && x.TransactionType == InventoryTransactionType.ProductionOutput);
        if (fromDate.HasValue) { materialTransactions = materialTransactions.Where(x => x.TransactionDate >= fromDate); outputTransactions = outputTransactions.Where(x => x.TransactionDate >= fromDate); }
        if (toDate.HasValue) { materialTransactions = materialTransactions.Where(x => x.TransactionDate <= toDate); outputTransactions = outputTransactions.Where(x => x.TransactionDate <= toDate); }
        if (productId.HasValue) { materialTransactions = materialTransactions.Where(x => x.ProductId == productId); outputTransactions = outputTransactions.Where(x => x.ProductId == productId); }
        if (warehouseId.HasValue) { materialTransactions = materialTransactions.Where(x => x.WarehouseId == warehouseId); outputTransactions = outputTransactions.Where(x => x.WarehouseId == warehouseId); }
        var orderRows = await orders.OrderByDescending(x => x.ProductionDate).Select(x => new ProductionReportRow { Number = x.Number, Product = x.Product!.Name, Warehouse = x.Warehouse!.Name, Quantity = x.ActualQuantity, Wastage = x.WastageQuantity, Cost = x.TotalCost, Date = x.ProductionDate, Status = x.Status }).ToListAsync();
        var consumptionRows = await materialTransactions.Include(x => x.Product).Include(x => x.Warehouse).OrderByDescending(x => x.TransactionDate).Select(x => new ProductionReportRow { Number = x.ReferenceId.ToString(), Product = x.Product!.Name, Warehouse = x.Warehouse!.Name, Quantity = -x.Quantity, Cost = -x.Quantity * x.UnitCost, Date = x.TransactionDate }).ToListAsync();
        var outputRows = await outputTransactions.Include(x => x.Product).Include(x => x.Warehouse).OrderByDescending(x => x.TransactionDate).Select(x => new ProductionReportRow { Number = x.ReferenceId.ToString(), Product = x.Product!.Name, Warehouse = x.Warehouse!.Name, Quantity = x.Quantity, Cost = x.Quantity * x.UnitCost, Date = x.TransactionDate }).ToListAsync();
        ViewBag.Summary = orderRows; ViewBag.Consumption = consumptionRows; ViewBag.Output = outputRows; await LoadLookupsAsync(); return View();
    }

    private async Task<bool> ValidateOrderInputAsync(ProductionOrder order)
    {
        if (order.PlannedQuantity <= 0) ModelState.AddModelError(nameof(order.PlannedQuantity), "Planned quantity must be greater than zero.");
        if (order.Materials is null || order.Materials.Count == 0) ModelState.AddModelError(nameof(order.Materials), "Add at least one material.");
        var product = await db.Products.FindAsync(order.ProductId);
        if (product is null || !product.IsActive || product.ItemType != ItemType.FinishedGood) ModelState.AddModelError(nameof(order.ProductId), "Select an active finished good.");
        if (await db.Warehouses.FindAsync(order.WarehouseId) is not Warehouse { IsActive: true }) ModelState.AddModelError(nameof(order.WarehouseId), "Select an active warehouse.");
        foreach (var material in order.Materials ?? [])
        {
            var raw = await db.Products.FindAsync(material.ProductId);
            if (raw is null || !raw.IsActive || raw.ItemType is not (ItemType.RawMaterial or ItemType.Consumable)) ModelState.AddModelError(nameof(order.Materials), "Materials must be active raw materials or consumables.");
            if (material.PlannedQuantity <= 0) ModelState.AddModelError(nameof(order.Materials), "Material planned quantities must be greater than zero.");
        }
        return ModelState.IsValid;
    }

    private async Task<IActionResult> ReturnCreateWithErrorsAsync(ProductionOrder order) { await LoadLookupsAsync(); return View("Create", order); }
    private async Task<IActionResult> TransitionAsync(Func<Task> transition, int id) { try { await transition(); TempData["Success"] = "Production status updated."; } catch (Exception exception) { TempData["Error"] = exception.Message; } return RedirectToAction(nameof(Details), new { id }); }
    private async Task LoadLookupsAsync() { ViewBag.Products = await db.Products.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(); ViewBag.Warehouses = await db.Warehouses.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(); }
}
