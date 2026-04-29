namespace WillVault.Application.DTOs.Auth;

public record LoginRequest(
    string Email,
    string Password,
    string? TwoFactorCode = null);
