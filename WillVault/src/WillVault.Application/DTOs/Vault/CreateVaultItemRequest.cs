using WillVault.Domain.Enums;

namespace WillVault.Application.DTOs.Vault;

public record CreateVaultItemRequest(
    string Title,
    string? Description,
    VaultItemType ItemType,
    string? ContentText);
