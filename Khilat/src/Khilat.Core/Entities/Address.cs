namespace Khilat.Core.Entities;

public class Address
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Country { get; set; } = "US";
    public bool IsDefault { get; set; }

    // Navigation
    public Customer Customer { get; set; } = null!;
}
