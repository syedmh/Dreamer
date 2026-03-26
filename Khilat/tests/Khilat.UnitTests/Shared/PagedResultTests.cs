using FluentAssertions;
using Khilat.Shared.DTOs.Common;

namespace Khilat.UnitTests.Shared;

public class PagedResultTests
{
    [Fact]
    public void TotalPages_CalculatesCorrectly()
    {
        var result = new PagedResult<string>
        {
            TotalCount = 25,
            PageSize = 10,
            Page = 1
        };

        result.TotalPages.Should().Be(3);
    }

    [Fact]
    public void TotalPages_ExactDivision()
    {
        var result = new PagedResult<string>
        {
            TotalCount = 20,
            PageSize = 10,
            Page = 1
        };

        result.TotalPages.Should().Be(2);
    }

    [Fact]
    public void HasPrevious_ReturnsFalse_OnFirstPage()
    {
        var result = new PagedResult<string>
        {
            TotalCount = 25,
            PageSize = 10,
            Page = 1
        };

        result.HasPrevious.Should().BeFalse();
    }

    [Fact]
    public void HasPrevious_ReturnsTrue_OnSecondPage()
    {
        var result = new PagedResult<string>
        {
            TotalCount = 25,
            PageSize = 10,
            Page = 2
        };

        result.HasPrevious.Should().BeTrue();
    }

    [Fact]
    public void HasNext_ReturnsFalse_OnLastPage()
    {
        var result = new PagedResult<string>
        {
            TotalCount = 25,
            PageSize = 10,
            Page = 3
        };

        result.HasNext.Should().BeFalse();
    }

    [Fact]
    public void HasNext_ReturnsTrue_WhenMorePages()
    {
        var result = new PagedResult<string>
        {
            TotalCount = 25,
            PageSize = 10,
            Page = 1
        };

        result.HasNext.Should().BeTrue();
    }
}
