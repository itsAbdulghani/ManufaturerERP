using System.Data;
using System.Security.Claims;
using ManufacturerERP.Data;
using ManufacturerERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ManufacturerERP.Services;

public interface ISalesService
{
    Task<string> NextNumberAsync(string prefix);
    Task ConfirmOrderAsync(int id, ClaimsPrincipal user);
    Task CompleteOrderAsync(int id, ClaimsPrincipal user);
    Task RecordPaymentAsync(CustomerPayment payment, ClaimsPrincipal user);
}

public class SalesService(ApplicationDbContext db, IAuditService audit) : ISalesService
{
    public async Task<string> NextNumberAsync(string prefix)
    {
        var count = prefix switch
        {
            "SO" => await db.SalesOrders.CountAsync(),
            "SI" => await db.CustomerInvoices.CountAsync(),
            _ => await db.CustomerPayments.CountAsync()
        };
        return $"{prefix}-{count + 1:000000}";
    }

    public async Task ConfirmOrderAsync(int id, ClaimsPrincipal user)
    {
        var order = await db.SalesOrders.FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new InvalidOperationException("Sales order not found.");
        if (order.Status != SalesOrderStatus.Draft)
            throw new InvalidOperationException("Only draft sales orders can be confirmed.");
        order.Status = SalesOrderStatus.Confirmed;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync(user, "Confirm", "SalesOrder", id, $"Confirmed {order.Number}; inventory unchanged");
    }

    public async Task CompleteOrderAsync(int id, ClaimsPrincipal user)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var order = await db.SalesOrders.Include(x => x.Customer).Include(x => x.Warehouse)
            .Include(x => x.Items).ThenInclude(x => x.Product)
            .FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new InvalidOperationException("Sales order not found.");
        if (order.Status != SalesOrderStatus.Confirmed)
            throw new InvalidOperationException("Only confirmed sales orders can be completed.");
        if (await db.CustomerInvoices.AnyAsync(x => x.SalesOrderId == id) ||
            await db.InventoryTransactions.AnyAsync(x => x.ReferenceType == "SalesOrder" && x.ReferenceId == id && x.TransactionType == InventoryTransactionType.Sale))
            throw new InvalidOperationException("Sales order completion has already been posted.");
        if (order.Items.Count == 0)
            throw new InvalidOperationException("Sales order must contain at least one item.");
        if (order.Customer is not { IsActive: true })
            throw new InvalidOperationException("The customer is inactive.");
        if (order.Warehouse is not { IsActive: true })
            throw new InvalidOperationException("The warehouse is inactive.");

        foreach (var line in order.Items)
        {
            if (line.Quantity <= 0)
                throw new InvalidOperationException("Sales quantities must be greater than zero.");
            if (line.Product is not { IsActive: true, ItemType: ItemType.FinishedGood })
                throw new InvalidOperationException("Sales order items must be active finished goods.");
        }

        foreach (var requirement in order.Items.GroupBy(x => x.ProductId))
        {
            var required = requirement.Sum(x => x.Quantity);
            var available = await db.InventoryTransactions
                .Where(x => x.ProductId == requirement.Key && x.WarehouseId == order.WarehouseId)
                .SumAsync(x => (decimal?)x.Quantity) ?? 0;
            if (required > available)
            {
                var productName = requirement.First().Product?.Name ?? "Finished good";
                throw new InvalidOperationException($"Insufficient finished-goods stock for {productName}. Required: {required}. Available: {available}.");
            }
        }

        foreach (var line in order.Items)
        {
            var positiveQuantity = await db.InventoryTransactions
                .Where(x => x.ProductId == line.ProductId && x.WarehouseId == order.WarehouseId && x.Quantity > 0)
                .SumAsync(x => (decimal?)x.Quantity) ?? 0;
            var positiveValue = await db.InventoryTransactions
                .Where(x => x.ProductId == line.ProductId && x.WarehouseId == order.WarehouseId && x.Quantity > 0)
                .SumAsync(x => (decimal?)(x.Quantity * x.UnitCost)) ?? 0;
            var unitCost = positiveQuantity > 0 ? positiveValue / positiveQuantity : 0;
            db.InventoryTransactions.Add(new InventoryTransaction
            {
                ProductId = line.ProductId,
                WarehouseId = order.WarehouseId,
                TransactionType = InventoryTransactionType.Sale,
                Quantity = -line.Quantity,
                UnitCost = unitCost,
                ReferenceType = "SalesOrder",
                ReferenceId = order.Id,
                TransactionDate = order.OrderDate,
                Notes = $"Dispatch {order.Number}",
                CreatedById = user.FindFirstValue(ClaimTypes.NameIdentifier)
            });
        }

        var invoice = new CustomerInvoice
        {
            Number = await NextNumberAsync("SI"),
            SalesOrderId = order.Id,
            CustomerId = order.CustomerId,
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(30)),
            Subtotal = order.Subtotal,
            Discount = order.Discount,
            Tax = order.Tax,
            GrandTotal = order.GrandTotal,
            PaidAmount = 0,
            OutstandingAmount = order.GrandTotal,
            Status = CustomerInvoiceStatus.Unpaid,
            Notes = order.Notes,
            CreatedById = user.FindFirstValue(ClaimTypes.NameIdentifier)
        };
        order.Status = SalesOrderStatus.Completed;
        order.CompletedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;
        db.CustomerInvoices.Add(invoice);
        await db.SaveChangesAsync();
        await audit.LogAsync(user, "Complete", "SalesOrder", order.Id, $"Completed {order.Number}; dispatched and invoiced {invoice.Number}");
        await transaction.CommitAsync();
    }

    public async Task RecordPaymentAsync(CustomerPayment payment, ClaimsPrincipal user)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var invoice = await db.CustomerInvoices.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == payment.CustomerInvoiceId)
            ?? throw new InvalidOperationException("Customer invoice not found.");
        if (invoice.Status is not (CustomerInvoiceStatus.Unpaid or CustomerInvoiceStatus.PartiallyPaid))
            throw new InvalidOperationException("Customer invoice is not open for payment.");
        if (payment.Amount <= 0)
            throw new InvalidOperationException("Payment amount must be greater than zero.");
        if (payment.Amount > invoice.OutstandingAmount)
            throw new InvalidOperationException("Payment cannot exceed the outstanding amount.");

        payment.Number = await NextNumberAsync("CPAY");
        payment.CustomerId = invoice.CustomerId;
        payment.CreatedById = user.FindFirstValue(ClaimTypes.NameIdentifier);
        invoice.PaidAmount += payment.Amount;
        invoice.OutstandingAmount -= payment.Amount;
        invoice.Status = invoice.OutstandingAmount == 0 ? CustomerInvoiceStatus.Paid : CustomerInvoiceStatus.PartiallyPaid;
        db.CustomerPayments.Add(payment);
        await db.SaveChangesAsync();
        await audit.LogAsync(user, "Create", "CustomerPayment", payment.Id, $"Recorded {payment.Number} against {invoice.Number}");
        await transaction.CommitAsync();
    }
}
