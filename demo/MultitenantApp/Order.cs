namespace MultitenantApp;

public class Order
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string TenantId { get; set; } = string.Empty;
}
