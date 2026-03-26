namespace Khilat.Shared.DTOs.Products;

public class SizeChartDto
{
    public string Size { get; set; } = string.Empty;
    public decimal FrontLengthInches { get; set; }
    public decimal BackLengthInches { get; set; }
    public decimal ArmLengthInches { get; set; }
    public decimal CuffInches { get; set; }
    public decimal ButtonLengthInches { get; set; }
    public decimal ShoulderInches { get; set; }
    public decimal ChestInches { get; set; }
    public decimal WaistInches { get; set; }
    public decimal HipInches { get; set; }
}
