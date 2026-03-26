namespace Khilat.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Khilat.IntegrationTests.Helpers;
using Khilat.Shared.DTOs.Auth;
using Khilat.Shared.DTOs.Common;

[Collection("Integration")]
public class AuthApiTests
{
    private readonly HttpClient _client;

    public AuthApiTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Register_ReturnsSuccess_WithValidData()
    {
        var request = new RegisterRequest
        {
            FirstName = "Test",
            LastName = "User",
            Email = $"register-success-{Guid.NewGuid():N}@test.com",
            Password = "TestPass1234",
            ConfirmPassword = "TestPass1234",
            Phone = "555-0100"
        };

        var response = await _client.PostAsJsonAsync("/api/auth/register", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Token.Should().NotBeNullOrWhiteSpace();
        result.Data.Email.Should().Be(request.Email);
        result.Data.Role.Should().Be("Customer");
    }

    [Fact]
    public async Task Register_ReturnsFail_WhenPasswordsDontMatch()
    {
        var request = new RegisterRequest
        {
            FirstName = "Test",
            LastName = "User",
            Email = $"register-mismatch-{Guid.NewGuid():N}@test.com",
            Password = "TestPass1234",
            ConfirmPassword = "DifferentPass1234",
            Phone = "555-0101"
        };

        var response = await _client.PostAsJsonAsync("/api/auth/register", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Errors.Should().Contain("Passwords do not match.");
    }

    [Fact]
    public async Task Login_ReturnsToken_WithValidCredentials()
    {
        var email = $"login-valid-{Guid.NewGuid():N}@test.com";
        const string password = "TestPass1234";

        // Register first
        var registerRequest = new RegisterRequest
        {
            FirstName = "Login",
            LastName = "Tester",
            Email = email,
            Password = password,
            ConfirmPassword = password
        };
        var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", registerRequest);
        registerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Then login
        var loginRequest = new LoginRequest { Email = email, Password = password };
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await loginResponse.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Token.Should().NotBeNullOrWhiteSpace();
        result.Data.Email.Should().Be(email);
    }

    [Fact]
    public async Task Login_ReturnsFail_WithInvalidCredentials()
    {
        var loginRequest = new LoginRequest
        {
            Email = "nonexistent@test.com",
            Password = "WrongPassword1"
        };

        var response = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
    }
}
