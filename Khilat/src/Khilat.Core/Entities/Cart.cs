namespace Khilat.Core.Entities;

public class Cart
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Customer Customer { get; set; } = null!;
    public ICollection<CartItem> Items { get; set; } = new List<CartItem>();

    public decimal TotalAmount => Items.Sum(i => i.Subtotal);
    public int TotalItems => Items.Sum(i => i.Quantity);
}
