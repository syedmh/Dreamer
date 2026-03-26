namespace Khilat.Shared.DTOs.Admin;

public class AdminDashboardDto
{
    public decimal TotalRevenue { get; set; }
    public int TotalOrders { get; set; }
    public int TotalCustomers { get; set; }
    public int PendingOrders { get; set; }
    public decimal RevenueThisMonth { get; set; }
    public int OrdersThisMonth { get; set; }
    public List<TopSellingVariantDto> TopSellingVariants { get; set; } = new();
    public List<RevenueByMonthDto> RevenueByMonth { get; set; } = new();
    public Dictionary<string, int> OrdersByStatus { get; set; } = new();
}

public class TopSellingVariantDto
{
    public string Color { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Craftsmanship { get; set; } = string.Empty;
    public int TotalSold { get; set; }
    public decimal TotalRevenue { get; set; }
}

public class RevenueByMonthDto
{
    public string Month { get; set; } = string.Empty;
    public decimal Revenue { get; set; }
    public int OrderCount { get; set; }
}
