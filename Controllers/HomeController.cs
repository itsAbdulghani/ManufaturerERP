using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ManufacturerERP.Models;
using ManufacturerERP.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ManufacturerERP.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly ApplicationDbContext db;
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext db)
    {
        _logger = logger;
        this.db = db;
    }

    public async Task<IActionResult> Index()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var inventory = await db.InventoryTransactions.GroupBy(x => x.ProductId).Select(x => new { ProductId = x.Key, Quantity = x.Sum(y => y.Quantity) }).ToDictionaryAsync(x => x.ProductId, x => x.Quantity);
        var rawMaterials = await db.Products.Where(x => x.IsActive && (x.ItemType == ItemType.RawMaterial || x.ItemType == ItemType.Consumable)).ToListAsync();
        ViewBag.Customers = await db.Customers.CountAsync(); ViewBag.Suppliers = await db.Suppliers.CountAsync(); ViewBag.Products = await db.Products.CountAsync(); ViewBag.ActiveProducts = await db.Products.CountAsync(x => x.IsActive); ViewBag.Warehouses = await db.Warehouses.CountAsync(); ViewBag.Users = await db.Users.CountAsync();
        ViewBag.ProductionsToday = await db.ProductionOrders.CountAsync(x => x.ProductionDate == today); ViewBag.ProductionsInProgress = await db.ProductionOrders.CountAsync(x => x.Status == ProductionOrderStatus.InProgress); ViewBag.ProductionsCompleted = await db.ProductionOrders.CountAsync(x => x.Status == ProductionOrderStatus.Completed); ViewBag.ProducedQuantity = await db.ProductionOrders.Where(x => x.Status == ProductionOrderStatus.Completed).SumAsync(x => (decimal?)x.ActualQuantity) ?? 0; ViewBag.ProductionCost = await db.ProductionOrders.Where(x => x.Status == ProductionOrderStatus.Completed).SumAsync(x => (decimal?)x.TotalCost) ?? 0; ViewBag.LowRawStock = rawMaterials.Count(x => (inventory.TryGetValue(x.Id, out var quantity) ? quantity : 0) <= x.MinimumStockLevel); ViewBag.FinishedGoodsStock = await db.InventoryTransactions.Where(x => x.Product!.ItemType == ItemType.FinishedGood).SumAsync(x => (decimal?)x.Quantity) ?? 0;
        ViewBag.SalesOrderCount = await db.SalesOrders.CountAsync(); ViewBag.CompletedSalesCount = await db.SalesOrders.CountAsync(x => x.Status == SalesOrderStatus.Completed); ViewBag.SalesAmount = await db.CustomerInvoices.SumAsync(x => (decimal?)x.GrandTotal) ?? 0; ViewBag.OutstandingReceivables = await db.CustomerInvoices.SumAsync(x => (decimal?)x.OutstandingAmount) ?? 0; ViewBag.CompletedSalesQuantity = await db.SalesOrderItems.Where(x => x.SalesOrder!.Status == SalesOrderStatus.Completed).SumAsync(x => (decimal?)x.Quantity) ?? 0;
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
