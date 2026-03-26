using FluentAssertions;
using Khilat.Shared.DTOs.Common;

namespace Khilat.UnitTests.Shared;

public class ApiResponseTests
{
    [Fact]
    public void Ok_ReturnsSuccessTrue_WithData()
    {
        var result = ApiResponse<string>.Ok("test-data");

        result.Success.Should().BeTrue();
        result.Data.Should().Be("test-data");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Ok_IncludesMessage_WhenProvided()
    {
        var result = ApiResponse<int>.Ok(42, "Operation successful");

        result.Success.Should().BeTrue();
        result.Data.Should().Be(42);
        result.Message.Should().Be("Operation successful");
    }

    [Fact]
    public void Fail_SingleError_ReturnsSuccessFalse()
    {
        var result = ApiResponse<string>.Fail("Something went wrong");

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("Something went wrong");
    }

    [Fact]
    public void Fail_MultipleErrors_ReturnsAllErrors()
    {
        var errors = new List<string> { "Error 1", "Error 2", "Error 3" };
        var result = ApiResponse<string>.Fail(errors);

        result.Success.Should().BeFalse();
        result.Errors.Should().HaveCount(3);
        result.Errors.Should().BeEquivalentTo(errors);
    }

    [Fact]
    public void Fail_DataIsNull()
    {
        var result = ApiResponse<string>.Fail("error");

        result.Data.Should().BeNull();
    }
}
