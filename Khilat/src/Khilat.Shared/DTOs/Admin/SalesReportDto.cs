namespace Khilat.Shared.DTOs.Admin;

public class SalesReportDto
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public decimal TotalRevenue { get; set; }
    public int TotalOrders { get; set; }
    public decimal AverageOrderValue { get; set; }
    public List<SalesReportItemDto> Items { get; set; } = new();
}

public class SalesReportItemDto
{
    public DateTime Date { get; set; }
    public int OrderCount { get; set; }
    public decimal Revenue { get; set; }
}
