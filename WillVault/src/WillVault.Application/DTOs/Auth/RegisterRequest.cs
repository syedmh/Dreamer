namespace WillVault.Application.DTOs.Auth;

public record RegisterRequest(
    string FullName,
    string Email,
    string Password,
    string ConfirmPassword,
    string? Phone,
    DateTime? DateOfBirth);
