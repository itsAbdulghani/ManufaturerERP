using System.Security.Claims;
using ManufacturerERP.Data;
using ManufacturerERP.Models;
using ManufacturerERP.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ManufacturerERP.Controllers;

[Authorize(Roles = "Admin,Manager,Sales,Accountant,Storekeeper")]
public class SalesController(ApplicationDbContext db, ISalesService workflow, IAuditService audit) : Controller
{
    public async Task<IActionResult> Index(string? search, SalesOrderStatus? status, int? customerId, int? warehouseId, DateOnly? fromDate, DateOnly? toDate)
    {
        var orders = db.SalesOrders.Include(x => x.Customer).Include(x => x.Warehouse).AsQueryable();
        if (status.HasValue) orders = orders.Where(x => x.Status == status.Value);
        if (customerId.HasValue) orders = orders.Where(x => x.CustomerId == customerId.Value);
        if (warehouseId.HasValue) orders = orders.Where(x => x.WarehouseId == warehouseId.Value);
        if (fromDate.HasValue) orders = orders.Where(x => x.OrderDate >= fromDate.Value);
        if (toDate.HasValue) orders = orders.Where(x => x.OrderDate <= toDate.Value);
        if (!string.IsNullOrWhiteSpace(search)) orders = orders.Where(x => x.Number.Contains(search) || x.Customer!.Name.Contains(search) || (x.Reference != null && x.Reference.Contains(search)));
        await LoadLookupsAsync();
        return View(await orders.OrderByDescending(x => x.CreatedAt).ToListAsync());
    }

    [HttpGet, Authorize(Roles = "Admin,Manager,Sales")]
    public async Task<IActionResult> Create()
    {
        await LoadLookupsAsync();
        return View("OrderForm", new SalesOrder { OrderDate = DateOnly.FromDateTime(DateTime.Today), Items = [new SalesOrderItem()] });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Sales")]
    public async Task<IActionResult> CreateForm(SalesOrder order)
    {
        ModelState.Remove(nameof(SalesOrder.Number));
        if (!await ValidateOrderAsync(order)) { await LoadLookupsAsync(); return View("OrderForm", order); }
        order.Number = await workflow.NextNumberAsync("SO");
        order.CreatedById = User.FindFirstValue(ClaimTypes.NameIdentifier);
        order.Status = SalesOrderStatus.Draft;
        Recalculate(order);
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "Create", "SalesOrder", order.Id, $"Created {order.Number}");
        TempData["Success"] = $"Sales order {order.Number} created.";
        return RedirectToAction(nameof(Details), new { id = order.Id });
    }

    [HttpGet, Authorize(Roles = "Admin,Manager,Sales")]
    public async Task<IActionResult> Edit(int id)
    {
        var order = await db.SalesOrders.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (order is null) return NotFound();
        if (order.Status != SalesOrderStatus.Draft) return BadRequest("Only draft sales orders can be edited.");
        await LoadLookupsAsync();
        return View("OrderForm", order);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Sales")]
    public async Task<IActionResult> EditForm(int id, SalesOrder input)
    {
        ModelState.Remove(nameof(SalesOrder.Number));
        var order = await db.SalesOrders.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (order is null) return NotFound();
        if (order.Status != SalesOrderStatus.Draft) return BadRequest("Only draft sales orders can be edited.");
        if (!await ValidateOrderAsync(input)) { input.Id = id; await LoadLookupsAsync(); return View("OrderForm", input); }
        order.CustomerId = input.CustomerId;
        order.WarehouseId = input.WarehouseId;
        order.OrderDate = input.OrderDate;
        order.RequestedDeliveryDate = input.RequestedDeliveryDate;
        order.Reference = input.Reference;
        order.Notes = input.Notes;
        order.Discount = input.Discount;
        order.Tax = input.Tax;
        order.UpdatedAt = DateTime.UtcNow;
        db.SalesOrderItems.RemoveRange(order.Items);
        order.Items = input.Items.Select(x => new SalesOrderItem { ProductId = x.ProductId, UnitId = x.UnitId, Quantity = x.Quantity, UnitPrice = x.UnitPrice, Discount = x.Discount, Tax = x.Tax }).ToList();
        Recalculate(order);
        await db.SaveChangesAsync();
        await audit.LogAsync(User, "Update", "SalesOrder", id, $"Updated {order.Number}");
        TempData["Success"] = $"Sales order {order.Number} updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var order = await db.SalesOrders.Include(x => x.Customer).Include(x => x.Warehouse)
            .Include(x => x.Items).ThenInclude(x => x.Product).ThenInclude(x => x!.Unit)
            .Include(x => x.Items).ThenInclude(x => x.Unit).Include(x => x.Invoice).ThenInclude(x => x!.Payments)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (order is null) return NotFound();
        ViewBag.Transactions = await db.InventoryTransactions.Where(x => x.ReferenceType == "SalesOrder" && x.ReferenceId == id)
            .Include(x => x.Product).OrderBy(x => x.Id).ToListAsync();
        return View(order);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Sales")]
    public async Task<IActionResult> Confirm(int id)
    {
        try { await workflow.ConfirmOrderAsync(id, User); TempData["Success"] = "Sales order confirmed. Inventory is unchanged until dispatch."; }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Sales,Storekeeper")]
    public async Task<IActionResult> Complete(int id)
    {
        try { await workflow.CompleteOrderAsync(id, User); TempData["Success"] = "Sales order dispatched; finished goods deducted and customer invoice created."; }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Invoices(string? search, CustomerInvoiceStatus? status, int? customerId)
    {
        var invoices = db.CustomerInvoices.Include(x => x.Customer).Include(x => x.SalesOrder).Include(x => x.Payments).AsQueryable();
        if (status.HasValue) invoices = invoices.Where(x => x.Status == status.Value);
        if (customerId.HasValue) invoices = invoices.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(search)) invoices = invoices.Where(x => x.Number.Contains(search) || x.Customer!.Name.Contains(search) || x.SalesOrder!.Number.Contains(search));
        ViewBag.Customers = await db.Customers.OrderBy(x => x.Name).ToListAsync();
        return View(await invoices.OrderByDescending(x => x.CreatedAt).ToListAsync());
    }

    public async Task<IActionResult> InvoiceDetails(int id)
    {
        var invoice = await db.CustomerInvoices.Include(x => x.Customer).Include(x => x.SalesOrder).Include(x => x.Payments)
            .FirstOrDefaultAsync(x => x.Id == id);
        return invoice is null ? NotFound() : View(invoice);
    }

    [HttpGet, Authorize(Roles = "Admin,Manager,Sales,Accountant")]
    public async Task<IActionResult> CreatePayment(int invoiceId)
    {
        var invoice = await db.CustomerInvoices.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == invoiceId);
        if (invoice is null) return NotFound();
        if (invoice.Status == CustomerInvoiceStatus.Paid) return BadRequest("Invoice is already paid.");
        ViewBag.Invoice = invoice;
        return View("PaymentForm", new CustomerPayment { CustomerInvoiceId = invoiceId, PaymentDate = DateOnly.FromDateTime(DateTime.Today) });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin,Manager,Sales,Accountant")]
    public async Task<IActionResult> CreatePaymentForm(CustomerPayment payment)
    {
        ModelState.Remove(nameof(CustomerPayment.Number));
        if (payment.Amount <= 0) ModelState.AddModelError(nameof(payment.Amount), "Payment amount must be greater than zero.");
        if (!ModelState.IsValid)
        {
            ViewBag.Invoice = await db.CustomerInvoices.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == payment.CustomerInvoiceId);
            return View("PaymentForm", payment);
        }
        try
        {
            await workflow.RecordPaymentAsync(payment, User);
            TempData["Success"] = "Customer payment recorded.";
            return RedirectToAction(nameof(InvoiceDetails), new { id = payment.CustomerInvoiceId });
        }
        catch (Exception exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            ViewBag.Invoice = await db.CustomerInvoices.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == payment.CustomerInvoiceId);
            return View("PaymentForm", payment);
        }
    }

    public async Task<IActionResult> Payments(string? search, int? customerId, DateOnly? fromDate, DateOnly? toDate)
    {
        var payments = db.CustomerPayments.Include(x => x.Customer).Include(x => x.CustomerInvoice).AsQueryable();
        if (customerId.HasValue) payments = payments.Where(x => x.CustomerId == customerId.Value);
        if (fromDate.HasValue) payments = payments.Where(x => x.PaymentDate >= fromDate.Value);
        if (toDate.HasValue) payments = payments.Where(x => x.PaymentDate <= toDate.Value);
        if (!string.IsNullOrWhiteSpace(search)) payments = payments.Where(x => x.Number.Contains(search) || x.Customer!.Name.Contains(search) || (x.ReferenceNumber != null && x.ReferenceNumber.Contains(search)));
        ViewBag.Customers = await db.Customers.OrderBy(x => x.Name).ToListAsync();
        return View(await payments.OrderByDescending(x => x.PaymentDate).ThenByDescending(x => x.CreatedAt).ToListAsync());
    }

    public async Task<IActionResult> Reports(DateOnly? fromDate, DateOnly? toDate, int? customerId, int? warehouseId, SalesOrderStatus? status)
    {
        var orders = db.SalesOrders.Include(x => x.Customer).Include(x => x.Warehouse).Include(x => x.Invoice).AsQueryable();
        if (fromDate.HasValue) orders = orders.Where(x => x.OrderDate >= fromDate.Value);
        if (toDate.HasValue) orders = orders.Where(x => x.OrderDate <= toDate.Value);
        if (customerId.HasValue) orders = orders.Where(x => x.CustomerId == customerId.Value);
        if (warehouseId.HasValue) orders = orders.Where(x => x.WarehouseId == warehouseId.Value);
        if (status.HasValue) orders = orders.Where(x => x.Status == status.Value);
        ViewBag.Rows = await orders.OrderByDescending(x => x.OrderDate).Select(x => new SalesReportRow
        {
            Number = x.Number,
            Customer = x.Customer!.Name,
            Warehouse = x.Warehouse!.Name,
            Quantity = x.Items.Sum(i => i.Quantity),
            Total = x.GrandTotal,
            Paid = x.Invoice == null ? 0 : x.Invoice.PaidAmount,
            Outstanding = x.Invoice == null ? 0 : x.Invoice.OutstandingAmount,
            Date = x.OrderDate,
            Status = x.Status
        }).ToListAsync();
        await LoadLookupsAsync();
        return View();
    }

    private async Task<bool> ValidateOrderAsync(SalesOrder order)
    {
        if (!await db.Customers.AnyAsync(x => x.Id == order.CustomerId && x.IsActive)) ModelState.AddModelError(nameof(order.CustomerId), "Select an active customer.");
        if (!await db.Warehouses.AnyAsync(x => x.Id == order.WarehouseId && x.IsActive)) ModelState.AddModelError(nameof(order.WarehouseId), "Select an active warehouse.");
        if (order.Items is null || order.Items.Count == 0) ModelState.AddModelError(nameof(order.Items), "Add at least one finished good.");
        if (order.Discount < 0 || order.Tax < 0) ModelState.AddModelError(string.Empty, "Discount and tax cannot be negative.");
        foreach (var item in order.Items ?? [])
        {
            var product = await db.Products.FindAsync(item.ProductId);
            if (product is null || !product.IsActive || product.ItemType != ItemType.FinishedGood) ModelState.AddModelError(nameof(order.Items), "Select active finished goods only.");
            else item.UnitId = product.UnitId;
            if (item.Quantity <= 0) ModelState.AddModelError(nameof(order.Items), "Sales quantities must be greater than zero.");
            if (item.UnitPrice < 0 || item.Discount < 0 || item.Tax < 0) ModelState.AddModelError(nameof(order.Items), "Prices, discounts, and tax cannot be negative.");
        }
        return ModelState.IsValid;
    }

    private static void Recalculate(SalesOrder order)
    {
        foreach (var item in order.Items) item.LineTotal = item.Quantity * item.UnitPrice - item.Discount + item.Tax;
        order.Subtotal = order.Items.Sum(x => x.Quantity * x.UnitPrice);
        order.GrandTotal = order.Items.Sum(x => x.LineTotal) - order.Discount + order.Tax;
    }

    private async Task LoadLookupsAsync()
    {
        ViewBag.Customers = await db.Customers.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync();
        ViewBag.Warehouses = await db.Warehouses.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync();
        ViewBag.Products = await db.Products.Include(x => x.Unit).Where(x => x.IsActive && x.ItemType == ItemType.FinishedGood).OrderBy(x => x.Name).ToListAsync();
    }
}
