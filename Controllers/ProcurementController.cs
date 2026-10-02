using ManufacturerERP.Data; using ManufacturerERP.Models; using ManufacturerERP.Services; using Microsoft.AspNetCore.Authorization; using Microsoft.AspNetCore.Mvc; using Microsoft.EntityFrameworkCore; using System.Security.Claims;
namespace ManufacturerERP.Controllers;
[Authorize(Roles="Admin,Manager,Purchase,Storekeeper,Accountant")]
public class ProcurementController(ApplicationDbContext db, IProcurementService workflow, IAuditService audit) : Controller
{
    public async Task<IActionResult> Index() => View(await db.PurchaseOrders.Include(x=>x.Supplier).Include(x=>x.Warehouse).OrderByDescending(x=>x.CreatedAt).ToListAsync());

    [HttpGet,Authorize(Roles="Admin,Manager,Purchase")]
    public async Task<IActionResult> CreateOrder()
    {
        await LoadOrderLookupsAsync();
        return View("OrderForm", new PurchaseOrder { OrderDate=DateOnly.FromDateTime(DateTime.Today), Items=[new PurchaseOrderItem()] });
    }

    [HttpPost,ValidateAntiForgeryToken,Authorize(Roles="Admin,Manager,Purchase")]
    public async Task<IActionResult> CreateOrderForm(PurchaseOrder order)
    {
        ModelState.Remove(nameof(PurchaseOrder.Number));
        if (!await ValidateOrderAsync(order)) { await LoadOrderLookupsAsync(); return View("OrderForm",order); }
        await SaveOrderAsync(order);
        TempData["Success"]=$"Purchase order {order.Number} created.";
        return RedirectToAction(nameof(OrderDetails),new {id=order.Id});
    }

    [HttpGet,Authorize(Roles="Admin,Manager,Purchase")]
    public async Task<IActionResult> EditOrder(int id)
    {
        var order=await db.PurchaseOrders.Include(x=>x.Items).FirstOrDefaultAsync(x=>x.Id==id);
        if(order is null)return NotFound();
        if(order.Status!=PurchaseOrderStatus.Draft)return BadRequest("Only draft purchase orders can be edited.");
        await LoadOrderLookupsAsync();
        return View("OrderForm",order);
    }

    [HttpPost,ValidateAntiForgeryToken,Authorize(Roles="Admin,Manager,Purchase")]
    public async Task<IActionResult> EditOrderForm(int id,PurchaseOrder input)
    {
        ModelState.Remove(nameof(PurchaseOrder.Number));
        var order=await db.PurchaseOrders.Include(x=>x.Items).FirstOrDefaultAsync(x=>x.Id==id);
        if(order is null)return NotFound();
        if(order.Status!=PurchaseOrderStatus.Draft)return BadRequest("Only draft purchase orders can be edited.");
        if(!await ValidateOrderAsync(input)){input.Id=id;await LoadOrderLookupsAsync();return View("OrderForm",input);}
        order.SupplierId=input.SupplierId;order.OrderDate=input.OrderDate;order.ExpectedDeliveryDate=input.ExpectedDeliveryDate;order.WarehouseId=input.WarehouseId;order.Reference=input.Reference;order.Notes=input.Notes;order.Discount=input.Discount;order.Tax=input.Tax;order.UpdatedAt=DateTime.UtcNow;
        db.PurchaseOrderItems.RemoveRange(order.Items);
        order.Items=input.Items.Select(x=>new PurchaseOrderItem{ProductId=x.ProductId,UnitId=x.UnitId,Quantity=x.Quantity,UnitPrice=x.UnitPrice,Discount=x.Discount,Tax=x.Tax,LineTotal=x.LineTotal}).ToList();
        RecalculateOrder(order);
        await db.SaveChangesAsync();
        await audit.LogAsync(User,"Update","PurchaseOrder",id,$"Updated {order.Number}");
        TempData["Success"]=$"Purchase order {order.Number} updated.";
        return RedirectToAction(nameof(OrderDetails),new{id});
    }

    public async Task<IActionResult> OrderDetails(int id)
    {
        var order=await db.PurchaseOrders.Include(x=>x.Supplier).Include(x=>x.Warehouse).Include(x=>x.Items).ThenInclude(x=>x.Product).ThenInclude(x=>x!.Unit).Include(x=>x.Items).ThenInclude(x=>x.Unit).FirstOrDefaultAsync(x=>x.Id==id);
        if(order is null)return NotFound();
        ViewBag.Receipts=await db.GoodsReceipts.Where(x=>x.PurchaseOrderId==id).OrderByDescending(x=>x.CreatedAt).ToListAsync();
        ViewBag.Invoices=await db.PurchaseInvoices.Where(x=>x.PurchaseOrderId==id).OrderByDescending(x=>x.CreatedAt).ToListAsync();
        return View(order);
    }

    [HttpPost,ValidateAntiForgeryToken,Authorize(Roles="Admin,Manager")]
    public async Task<IActionResult> ApproveOrderForm(int id)
    {
        try{await workflow.ApproveOrderAsync(id,User);TempData["Success"]="Purchase order approved.";}
        catch(Exception exception){TempData["Error"]=exception.Message;}
        return RedirectToAction(nameof(OrderDetails),new{id});
    }

    [HttpGet,Authorize(Roles="Admin,Manager,Storekeeper")]
    public async Task<IActionResult> CreateReceipt(int id)
    {
        var order=await db.PurchaseOrders.Include(x=>x.Items).ThenInclude(x=>x.Product).ThenInclude(x=>x!.Unit).FirstOrDefaultAsync(x=>x.Id==id);
        if(order is null)return NotFound();
        if(order.Status is not(PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived))return BadRequest("Order cannot be received.");
        var receipt=new GoodsReceipt{PurchaseOrderId=id,ReceiptDate=DateOnly.FromDateTime(DateTime.Today),Items=order.Items.Where(x=>x.ReceivedQuantity<x.Quantity).Select(x=>new GoodsReceiptItem{PurchaseOrderItemId=x.Id,OrderedQuantity=x.Quantity,PreviouslyReceivedQuantity=x.ReceivedQuantity,Product=x.Product,Unit=x.Unit,UnitPrice=x.UnitPrice}).ToList()};
        return View("ReceiptForm",receipt);
    }

    [HttpPost,ValidateAntiForgeryToken,Authorize(Roles="Admin,Manager,Storekeeper")]
    public async Task<IActionResult> CreateReceiptForm(GoodsReceipt receipt)
    {
        ModelState.Remove(nameof(GoodsReceipt.Number));
        var order=await db.PurchaseOrders.Include(x=>x.Items).FirstOrDefaultAsync(x=>x.Id==receipt.PurchaseOrderId);
        if(order is null)return NotFound();
        if(order.Status is not(PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived))ModelState.AddModelError(string.Empty,"Order cannot be received.");
        if(receipt.Items.Count==0)ModelState.AddModelError(nameof(receipt.Items),"Enter a positive quantity for at least one order line.");
        for(var index=0;index<receipt.Items.Count;index++)
        {
            var item=receipt.Items[index];
            if(item.ReceivingQuantity<0)ModelState.AddModelError($"Items[{index}].ReceivingQuantity","Receiving quantity cannot be negative.");
            if(item.ReceivingQuantity==0)continue;
            var orderItem=order.Items.SingleOrDefault(x=>x.Id==item.PurchaseOrderItemId);
            if(orderItem is null||item.ReceivingQuantity>orderItem.Quantity-orderItem.ReceivedQuantity)ModelState.AddModelError($"Items[{index}].ReceivingQuantity","Quantity exceeds the open purchase-order quantity.");
        }
        if(!receipt.Items.Any(x=>x.ReceivingQuantity>0))ModelState.AddModelError(nameof(receipt.Items),"Enter a positive quantity for at least one order line.");
        if(!ModelState.IsValid){await LoadReceiptLinesAsync(receipt);return View("ReceiptForm",receipt);}
        receipt.Items=receipt.Items.Where(x=>x.ReceivingQuantity>0).ToList();
        await SaveReceiptAsync(receipt,order);
        TempData["Success"]=$"Goods receipt {receipt.Number} created. Confirm it to post stock.";
        return RedirectToAction(nameof(OrderDetails),new{id=receipt.PurchaseOrderId});
    }

    [HttpPost,ValidateAntiForgeryToken,Authorize(Roles="Admin,Manager,Storekeeper")]
    public async Task<IActionResult> ConfirmReceiptForm(int id,int orderId)
    {
        try{await workflow.ConfirmReceiptAsync(id,User);TempData["Success"]="Goods receipt confirmed and inventory posted.";}
        catch(Exception exception){TempData["Error"]=exception.Message;}
        return RedirectToAction(nameof(OrderDetails),new{id=orderId});
    }

    public async Task<IActionResult> Invoices()
    {
        var invoices=await db.PurchaseInvoices.Include(x=>x.Supplier).Include(x=>x.PurchaseOrder).Include(x=>x.Payments).OrderByDescending(x=>x.CreatedAt).ToListAsync();
        return View(invoices);
    }

    [HttpGet,Authorize(Roles="Admin,Manager,Purchase,Accountant")]
    public async Task<IActionResult> CreateInvoice(int? purchaseOrderId)
    {
        await LoadInvoiceLookupsAsync();
        var invoice=new PurchaseInvoice{InvoiceDate=DateOnly.FromDateTime(DateTime.Today),DueDate=DateOnly.FromDateTime(DateTime.Today.AddDays(30)),Items=[new PurchaseInvoiceItem()]};
        if(purchaseOrderId.HasValue){var order=await db.PurchaseOrders.Include(x=>x.Items).FirstOrDefaultAsync(x=>x.Id==purchaseOrderId.Value);if(order is not null){invoice.PurchaseOrderId=order.Id;invoice.SupplierId=order.SupplierId;invoice.WarehouseId=order.WarehouseId;invoice.Items=order.Items.Select(x=>new PurchaseInvoiceItem{ProductId=x.ProductId,UnitId=x.UnitId,Quantity=x.ReceivedQuantity>0?x.ReceivedQuantity:x.Quantity,UnitPrice=x.UnitPrice}).ToList();}}
        return View("InvoiceForm",invoice);
    }

    [HttpPost,ValidateAntiForgeryToken,Authorize(Roles="Admin,Manager,Purchase,Accountant")]
    public async Task<IActionResult> CreateInvoiceForm(PurchaseInvoice invoice)
    {
        ModelState.Remove(nameof(PurchaseInvoice.Number));
        if(invoice.Items.Count==0)ModelState.AddModelError(nameof(invoice.Items),"Add at least one invoice line.");
        if(!await db.Suppliers.AnyAsync(x=>x.Id==invoice.SupplierId&&x.IsActive))ModelState.AddModelError(nameof(invoice.SupplierId),"Select an active supplier.");
        if(invoice.Items.Any(x=>x.Quantity<=0))ModelState.AddModelError(nameof(invoice.Items),"Invoice quantities must be greater than zero.");
        if(invoice.Items.Any(x=>x.UnitPrice<0||x.Discount<0||x.Tax<0))ModelState.AddModelError(nameof(invoice.Items),"Prices, discounts, and tax cannot be negative.");
        if(invoice.PurchaseOrderId.HasValue){var po=await db.PurchaseOrders.FindAsync(invoice.PurchaseOrderId.Value);if(po is null||po.SupplierId!=invoice.SupplierId)ModelState.AddModelError(nameof(invoice.PurchaseOrderId),"The selected order must belong to the selected supplier.");}
        foreach(var item in invoice.Items){var product=await db.Products.FindAsync(item.ProductId);if(product is null||!product.IsActive)ModelState.AddModelError(nameof(invoice.Items),"Select active products for invoice lines.");else item.UnitId=product.UnitId;}
        if(!ModelState.IsValid){await LoadInvoiceLookupsAsync();return View("InvoiceForm",invoice);}
        await SaveInvoiceAsync(invoice);
        TempData["Success"]=$"Purchase invoice {invoice.Number} created. Confirm it to establish the payable.";
        return RedirectToAction(nameof(Invoices));
    }

    [HttpPost,ValidateAntiForgeryToken,Authorize(Roles="Admin,Manager,Purchase,Accountant")]
    public async Task<IActionResult> ConfirmInvoiceForm(int id)
    {
        try{await workflow.ConfirmInvoiceAsync(id,User);TempData["Success"]="Invoice confirmed; payable established.";}
        catch(Exception exception){TempData["Error"]=exception.Message;}
        return RedirectToAction(nameof(Invoices));
    }

    [HttpGet,Authorize(Roles="Admin,Manager,Accountant")]
    public async Task<IActionResult> RecordPayment(int invoiceId)
    {
        var invoice=await db.PurchaseInvoices.Include(x=>x.Supplier).Include(x=>x.Payments).FirstOrDefaultAsync(x=>x.Id==invoiceId);
        if(invoice is null)return NotFound();
        ViewBag.Invoice=invoice;ViewBag.Outstanding=invoice.GrandTotal-invoice.Payments.Sum(x=>x.Amount);
        return View("PaymentForm",new SupplierPayment{PurchaseInvoiceId=invoiceId,PaymentDate=DateOnly.FromDateTime(DateTime.Today),PaymentMethod=PaymentMethod.BankTransfer});
    }

    [HttpPost,ValidateAntiForgeryToken,Authorize(Roles="Admin,Manager,Accountant")]
    public async Task<IActionResult> RecordPaymentForm(SupplierPayment payment)
    {
        ModelState.Remove(nameof(SupplierPayment.Number));
        if(payment.Amount<=0)ModelState.AddModelError(nameof(payment.Amount),"Payment amount must be greater than zero.");
        if(!ModelState.IsValid){await LoadPaymentInvoiceAsync(payment.PurchaseInvoiceId);return View("PaymentForm",payment);}
        try{await workflow.RecordPaymentAsync(payment,User);TempData["Success"]="Supplier payment recorded.";return RedirectToAction(nameof(Invoices));}
        catch(Exception exception){ModelState.AddModelError(string.Empty,exception.Message);await LoadPaymentInvoiceAsync(payment.PurchaseInvoiceId);return View("PaymentForm",payment);}
    }
    [HttpPost,Authorize(Roles="Admin,Manager,Purchase")] public async Task<IActionResult> CreateOrder([FromBody] PurchaseOrder order) { if(order.Items.Count==0)return BadRequest("An order needs at least one item."); order.Number=await workflow.NextNumberAsync("PO"); order.CreatedById=User.FindFirstValue(ClaimTypes.NameIdentifier); foreach(var i in order.Items){i.LineTotal=i.Quantity*i.UnitPrice-i.Discount+i.Tax;} order.Subtotal=order.Items.Sum(x=>x.Quantity*x.UnitPrice);order.GrandTotal=order.Items.Sum(x=>x.LineTotal)-order.Discount+order.Tax;db.PurchaseOrders.Add(order);await db.SaveChangesAsync();await audit.LogAsync(User,"Create","PurchaseOrder",order.Id,$"Created {order.Number}");return Ok(order); }
    [HttpPost,Authorize(Roles="Admin,Manager")] public async Task<IActionResult> ApproveOrder(int id) { await workflow.ApproveOrderAsync(id,User);return Ok(); }
    [HttpPost,Authorize(Roles="Admin,Manager,Storekeeper")] public async Task<IActionResult> CreateReceipt([FromBody] GoodsReceipt receipt) { var po=await db.PurchaseOrders.Include(x=>x.Items).FirstOrDefaultAsync(x=>x.Id==receipt.PurchaseOrderId);if(po is null)return BadRequest("Purchase order not found."); if(po.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived))return BadRequest("Order cannot be received.");receipt.Number=await workflow.NextNumberAsync("GR");receipt.SupplierId=po.SupplierId;receipt.WarehouseId=po.WarehouseId;receipt.CreatedById=User.FindFirstValue(ClaimTypes.NameIdentifier);foreach(var item in receipt.Items){var poi=po.Items.SingleOrDefault(x=>x.Id==item.PurchaseOrderItemId);if(poi is null||item.ReceivingQuantity<=0||item.ReceivingQuantity>poi.Quantity-poi.ReceivedQuantity)return BadRequest("Invalid receiving quantity.");item.ProductId=poi.ProductId;item.UnitId=poi.UnitId;item.OrderedQuantity=poi.Quantity;item.PreviouslyReceivedQuantity=poi.ReceivedQuantity;item.UnitPrice=poi.UnitPrice;}db.GoodsReceipts.Add(receipt);await db.SaveChangesAsync();await audit.LogAsync(User,"Create","GoodsReceipt",receipt.Id,$"Created {receipt.Number}");return Ok(receipt); }
    [HttpPost,Authorize(Roles="Admin,Manager,Storekeeper")] public async Task<IActionResult> ConfirmReceipt(int id) { await workflow.ConfirmReceiptAsync(id,User);return Ok(); }
    [HttpPost,Authorize(Roles="Admin,Manager,Purchase,Accountant")] public async Task<IActionResult> CreateInvoice([FromBody] PurchaseInvoice invoice) { invoice.Number=await workflow.NextNumberAsync("PI");invoice.CreatedById=User.FindFirstValue(ClaimTypes.NameIdentifier);foreach(var x in invoice.Items)x.LineTotal=x.Quantity*x.UnitPrice-x.Discount+x.Tax;invoice.Subtotal=invoice.Items.Sum(x=>x.Quantity*x.UnitPrice);invoice.GrandTotal=invoice.Items.Sum(x=>x.LineTotal)-invoice.Discount+invoice.Tax;db.PurchaseInvoices.Add(invoice);await db.SaveChangesAsync();await audit.LogAsync(User,"Create","PurchaseInvoice",invoice.Id,$"Created {invoice.Number}");return Ok(invoice); }
    [HttpPost,Authorize(Roles="Admin,Manager,Purchase,Accountant")] public async Task<IActionResult> ConfirmInvoice(int id) { await workflow.ConfirmInvoiceAsync(id,User);return Ok(); }
    [HttpPost,Authorize(Roles="Admin,Manager,Accountant")] public async Task<IActionResult> RecordPayment([FromBody] SupplierPayment payment) { await workflow.RecordPaymentAsync(payment,User);return Ok(payment); }
    public async Task<IActionResult> Inventory(int? productId,int? warehouseId) { var q=db.InventoryTransactions.Include(x=>x.Product).ThenInclude(x=>x!.Unit).Include(x=>x.Warehouse).AsQueryable();if(productId.HasValue)q=q.Where(x=>x.ProductId==productId);if(warehouseId.HasValue)q=q.Where(x=>x.WarehouseId==warehouseId);return View(await q.OrderByDescending(x=>x.TransactionDate).ToListAsync()); }

    private async Task LoadOrderLookupsAsync()
    {
        ViewBag.Suppliers=await db.Suppliers.Where(x=>x.IsActive).OrderBy(x=>x.Name).ToListAsync();
        ViewBag.Warehouses=await db.Warehouses.Where(x=>x.IsActive).OrderBy(x=>x.Name).ToListAsync();
        ViewBag.Products=await db.Products.Include(x=>x.Unit).Where(x=>x.IsActive&&(x.ItemType==ItemType.RawMaterial||x.ItemType==ItemType.Consumable)).OrderBy(x=>x.Name).ToListAsync();
    }

    private async Task<bool> ValidateOrderAsync(PurchaseOrder order)
    {
        if(!await db.Suppliers.AnyAsync(x=>x.Id==order.SupplierId&&x.IsActive))ModelState.AddModelError(nameof(order.SupplierId),"Select an active supplier.");
        if(!await db.Warehouses.AnyAsync(x=>x.Id==order.WarehouseId&&x.IsActive))ModelState.AddModelError(nameof(order.WarehouseId),"Select an active warehouse.");
        if(order.Items is null||order.Items.Count==0)ModelState.AddModelError(nameof(order.Items),"Add at least one item.");
        if(order.Discount<0||order.Tax<0)ModelState.AddModelError(string.Empty,"Order discount and tax cannot be negative.");
        foreach(var item in order.Items??[]){var product=await db.Products.FindAsync(item.ProductId);if(product is null||!product.IsActive||product.ItemType is not(ItemType.RawMaterial or ItemType.Consumable))ModelState.AddModelError(nameof(order.Items),"Select active raw materials or consumables.");else item.UnitId=product.UnitId;if(item.Quantity<=0)ModelState.AddModelError(nameof(order.Items),"Order quantities must be greater than zero.");if(item.UnitPrice<0||item.Discount<0||item.Tax<0)ModelState.AddModelError(nameof(order.Items),"Prices, discounts, and tax cannot be negative.");}
        return ModelState.IsValid;
    }

    private async Task SaveOrderAsync(PurchaseOrder order)
    {
        order.Number=await workflow.NextNumberAsync("PO");
        order.CreatedById=User.FindFirstValue(ClaimTypes.NameIdentifier);
        order.Status=PurchaseOrderStatus.Draft;
        RecalculateOrder(order);
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        await audit.LogAsync(User,"Create","PurchaseOrder",order.Id,$"Created {order.Number}");
    }

    private static void RecalculateOrder(PurchaseOrder order)
    {
        foreach(var item in order.Items)item.LineTotal=item.Quantity*item.UnitPrice-item.Discount+item.Tax;
        order.Subtotal=order.Items.Sum(x=>x.Quantity*x.UnitPrice);
        order.GrandTotal=order.Items.Sum(x=>x.LineTotal)-order.Discount+order.Tax;
    }

    private async Task LoadReceiptLinesAsync(GoodsReceipt receipt)
    {
        var order=await db.PurchaseOrders.Include(x=>x.Items).ThenInclude(x=>x.Product).ThenInclude(x=>x!.Unit).Include(x=>x.Items).ThenInclude(x=>x.Unit).FirstOrDefaultAsync(x=>x.Id==receipt.PurchaseOrderId);
        if(order is null)return;
        foreach(var line in receipt.Items){var orderLine=order.Items.FirstOrDefault(x=>x.Id==line.PurchaseOrderItemId);if(orderLine is not null){line.Product=orderLine.Product;line.Unit=orderLine.Unit;line.OrderedQuantity=orderLine.Quantity;line.PreviouslyReceivedQuantity=orderLine.ReceivedQuantity;line.UnitPrice=orderLine.UnitPrice;}}
    }

    private async Task SaveReceiptAsync(GoodsReceipt receipt,PurchaseOrder order)
    {
        receipt.Number=await workflow.NextNumberAsync("GR");receipt.SupplierId=order.SupplierId;receipt.WarehouseId=order.WarehouseId;receipt.CreatedById=User.FindFirstValue(ClaimTypes.NameIdentifier);
        foreach(var line in receipt.Items){var orderLine=order.Items.Single(x=>x.Id==line.PurchaseOrderItemId);line.ProductId=orderLine.ProductId;line.UnitId=orderLine.UnitId;line.OrderedQuantity=orderLine.Quantity;line.PreviouslyReceivedQuantity=orderLine.ReceivedQuantity;line.UnitPrice=orderLine.UnitPrice;}
        db.GoodsReceipts.Add(receipt);await db.SaveChangesAsync();await audit.LogAsync(User,"Create","GoodsReceipt",receipt.Id,$"Created {receipt.Number}");
    }

    private async Task LoadInvoiceLookupsAsync()
    {
        ViewBag.Suppliers=await db.Suppliers.Where(x=>x.IsActive).OrderBy(x=>x.Name).ToListAsync();
        ViewBag.Warehouses=await db.Warehouses.Where(x=>x.IsActive).OrderBy(x=>x.Name).ToListAsync();
        ViewBag.Products=await db.Products.Include(x=>x.Unit).Where(x=>x.IsActive).OrderBy(x=>x.Name).ToListAsync();
        ViewBag.PurchaseOrders=await db.PurchaseOrders.Where(x=>x.Status==PurchaseOrderStatus.Approved||x.Status==PurchaseOrderStatus.PartiallyReceived||x.Status==PurchaseOrderStatus.Received).OrderByDescending(x=>x.CreatedAt).ToListAsync();
    }

    private async Task SaveInvoiceAsync(PurchaseInvoice invoice)
    {
        invoice.Number=await workflow.NextNumberAsync("PI");invoice.CreatedById=User.FindFirstValue(ClaimTypes.NameIdentifier);invoice.Status=PurchaseInvoiceStatus.Draft;
        foreach(var line in invoice.Items)line.LineTotal=line.Quantity*line.UnitPrice-line.Discount+line.Tax;
        invoice.Subtotal=invoice.Items.Sum(x=>x.Quantity*x.UnitPrice);invoice.GrandTotal=invoice.Items.Sum(x=>x.LineTotal)-invoice.Discount+invoice.Tax;
        db.PurchaseInvoices.Add(invoice);await db.SaveChangesAsync();await audit.LogAsync(User,"Create","PurchaseInvoice",invoice.Id,$"Created {invoice.Number}");
    }

    private async Task LoadPaymentInvoiceAsync(int invoiceId)
    {
        var invoice=await db.PurchaseInvoices.Include(x=>x.Supplier).Include(x=>x.Payments).FirstOrDefaultAsync(x=>x.Id==invoiceId);
        ViewBag.Invoice=invoice;ViewBag.Outstanding=invoice is null?0:invoice.GrandTotal-invoice.Payments.Sum(x=>x.Amount);
    }
}
