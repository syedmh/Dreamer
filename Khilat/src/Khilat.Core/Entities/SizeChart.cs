namespace Khilat.Core.Entities;

using Khilat.Core.Enums;

public class SizeChart
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public ProductSize Size { get; set; }
    public decimal FrontLengthInches { get; set; }
    public decimal BackLengthInches { get; set; }
    public decimal ArmLengthInches { get; set; }
    public decimal CuffInches { get; set; }
    public decimal ButtonLengthInches { get; set; }
    public decimal ShoulderInches { get; set; }
    public decimal ChestInches { get; set; }
    public decimal WaistInches { get; set; }
    public decimal HipInches { get; set; }

    // Navigation
    public Product Product { get; set; } = null!;
}
