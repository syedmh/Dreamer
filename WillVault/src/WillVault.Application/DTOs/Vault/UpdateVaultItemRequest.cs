namespace WillVault.Application.DTOs.Vault;

public record UpdateVaultItemRequest(
    string Title,
    string? Description,
    string? ContentText);
