using WillVault.Domain.Enums;

namespace WillVault.Application.DTOs.Auth;

public record UserDto(
    Guid Id,
    string FullName,
    string Email,
    string? Phone,
    AccountStatus AccountStatus);
