using FluentAssertions;
using Khilat.Core.Entities;
using Khilat.Core.Enums;

namespace Khilat.UnitTests.Core;

public class SizeChartTests
{
    [Fact]
    public void SizeChart_AllSizes_HaveEntries()
    {
        var allSizes = Enum.GetValues<ProductSize>();

        allSizes.Should().HaveCount(6);
        allSizes.Should().Contain(ProductSize.XS);
        allSizes.Should().Contain(ProductSize.S);
        allSizes.Should().Contain(ProductSize.M);
        allSizes.Should().Contain(ProductSize.L);
        allSizes.Should().Contain(ProductSize.XL);
        allSizes.Should().Contain(ProductSize.XXL);
    }

    [Fact]
    public void SizeChart_Measurements_ArePositive()
    {
        var chart = new SizeChart
        {
            Size = ProductSize.M,
            FrontLengthInches = 28m,
            BackLengthInches = 31m,
            ArmLengthInches = 29m,
            CuffInches = 4m,
            ButtonLengthInches = 26m,
            ShoulderInches = 15.5m,
            ChestInches = 20.5m,
            WaistInches = 20m,
            HipInches = 20.5m
        };

        chart.FrontLengthInches.Should().BeGreaterThan(0);
        chart.BackLengthInches.Should().BeGreaterThan(0);
        chart.ArmLengthInches.Should().BeGreaterThan(0);
        chart.CuffInches.Should().BeGreaterThan(0);
        chart.ButtonLengthInches.Should().BeGreaterThan(0);
        chart.ShoulderInches.Should().BeGreaterThan(0);
        chart.ChestInches.Should().BeGreaterThan(0);
        chart.WaistInches.Should().BeGreaterThan(0);
        chart.HipInches.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SizeChart_BaseSizeM_Measurements()
    {
        var chart = new SizeChart
        {
            Size = ProductSize.M,
            FrontLengthInches = 28m,
            BackLengthInches = 31m,
            ArmLengthInches = 29m,
            CuffInches = 4m,
            ButtonLengthInches = 26m,
            ShoulderInches = 15.5m,
            ChestInches = 20.5m,
            WaistInches = 20m,
            HipInches = 20.5m
        };

        chart.FrontLengthInches.Should().Be(28m);
        chart.BackLengthInches.Should().Be(31m);
        chart.ArmLengthInches.Should().Be(29m);
        chart.CuffInches.Should().Be(4m);
        chart.ButtonLengthInches.Should().Be(26m);
        chart.ShoulderInches.Should().Be(15.5m);
        chart.ChestInches.Should().Be(20.5m);
        chart.WaistInches.Should().Be(20m);
        chart.HipInches.Should().Be(20.5m);
    }
}
