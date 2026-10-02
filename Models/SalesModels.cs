using System.ComponentModel.DataAnnotations;

namespace ManufacturerERP.Models;

public enum SalesOrderStatus { Draft, Confirmed, Completed }
public enum CustomerInvoiceStatus { Unpaid, PartiallyPaid, Paid }

public class SalesOrder
{
    public int Id { get; set; }
    [Required, StringLength(30)] public string Number { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public DateOnly OrderDate { get; set; }
    public DateOnly? RequestedDeliveryDate { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public SalesOrderStatus Status { get; set; } = SalesOrderStatus.Draft;
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public decimal GrandTotal { get; set; }
    public string? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<SalesOrderItem> Items { get; set; } = [];
    public CustomerInvoice? Invoice { get; set; }
}

public class SalesOrderItem
{
    public int Id { get; set; }
    public int SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int UnitId { get; set; }
    public Unit? Unit { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public decimal LineTotal { get; set; }
}

public class CustomerInvoice
{
    public int Id { get; set; }
    [Required, StringLength(30)] public string Number { get; set; } = "";
    public int SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public CustomerInvoiceStatus Status { get; set; } = CustomerInvoiceStatus.Unpaid;
    public string? Notes { get; set; }
    public string? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<CustomerPayment> Payments { get; set; } = [];
}

public class CustomerPayment
{
    public int Id { get; set; }
    [Required, StringLength(30)] public string Number { get; set; } = "";
    public int CustomerInvoiceId { get; set; }
    public CustomerInvoice? CustomerInvoice { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateOnly PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.BankTransfer;
    public string? ReferenceNumber { get; set; }
    public string? Notes { get; set; }
    public string? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SalesReportRow
{
    public string Number { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Total { get; set; }
    public decimal Paid { get; set; }
    public decimal Outstanding { get; set; }
    public DateOnly Date { get; set; }
    public SalesOrderStatus Status { get; set; }
}
