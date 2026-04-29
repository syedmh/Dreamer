using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WillVault.Application.DTOs.Common;
using WillVault.Application.DTOs.Recipients;
using WillVault.Application.DTOs.Vault;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.API.Controllers;

[ApiController]
[Route("api/vault")]
[Authorize]
public class VaultController : ControllerBase
{
    private readonly IVaultItemRepository _vaultItemRepository;
    private readonly IVaultOwnerRepository _vaultOwnerRepository;
    private readonly IStorageProvider _storageProvider;
    private readonly IEncryptionService _encryptionService;
    private readonly IAuditService _auditService;

    public VaultController(
        IVaultItemRepository vaultItemRepository,
        IVaultOwnerRepository vaultOwnerRepository,
        IStorageProvider storageProvider,
        IEncryptionService encryptionService,
        IAuditService auditService)
    {
        _vaultItemRepository = vaultItemRepository;
        _vaultOwnerRepository = vaultOwnerRepository;
        _storageProvider = storageProvider;
        _encryptionService = encryptionService;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] VaultItemType? type = null)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        if (type.HasValue)
        {
            var items = await _vaultItemRepository.GetByOwnerIdAndTypeAsync(owner.Id, type.Value);
            var dtos = items.Select(MapToDto).ToList();
            return Ok(new PagedResult<VaultItemDto>(dtos, dtos.Count, 1, dtos.Count));
        }

        var (vaultItems, totalCount) = await _vaultItemRepository.GetByOwnerIdAsync(owner.Id, page, pageSize);
        var pagedDtos = vaultItems.Select(MapToDto).ToList();
        return Ok(new PagedResult<VaultItemDto>(pagedDtos, totalCount, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = await _vaultItemRepository.GetWithRecipientsAsync(id);
        if (item is null || item.OwnerId != owner.Id)
            return NotFound();

        var recipients = item.VaultItemRecipients.Select(vir => new RecipientDto(
            vir.Recipient.Id, vir.Recipient.OwnerId, vir.Recipient.FullName,
            vir.Recipient.Email, vir.Recipient.Phone, vir.Recipient.Relationship,
            vir.Recipient.IsEmailVerified, vir.Recipient.IsPhoneVerified,
            vir.Recipient.EmailVerifiedAt, vir.Recipient.PhoneVerifiedAt,
            vir.Recipient.CreatedAt, vir.Recipient.UpdatedAt)).ToList();

        var detail = new VaultItemDetailDto(
            item.Id, item.OwnerId, item.Title, item.Description, item.ItemType,
            item.OriginalFileName, item.ContentType, item.FileSizeBytes, item.IsArchived,
            item.ExecutorId, item.LegalNotes, item.SpecialInstructions,
            item.VaultItemRecipients.Count, recipients, item.CreatedAt, item.UpdatedAt);

        return Ok(detail);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateVaultItemRequest request)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = new VaultItem
        {
            OwnerId = owner.Id,
            Title = request.Title,
            Description = request.Description,
            ItemType = request.ItemType,
            ContentText = request.ContentText
        };

        await _vaultItemRepository.AddAsync(item);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "VaultItemCreated",
            "VaultItem", item.Id);

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, MapToDto(item));
    }

    [HttpPost("upload")]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] string title,
        [FromForm] string? description,
        [FromForm] VaultItemType itemType)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        using var stream = file.OpenReadStream();
        var storagePath = await _storageProvider.UploadAsync(stream, file.FileName, file.ContentType);

        var item = new VaultItem
        {
            OwnerId = owner.Id,
            Title = title,
            Description = description,
            ItemType = itemType,
            ContentPath = storagePath,
            OriginalFileName = file.FileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length
        };

        await _vaultItemRepository.AddAsync(item);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "VaultItemUploaded",
            "VaultItem", item.Id);

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, MapToDto(item));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateVaultItemRequest request)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = await _vaultItemRepository.GetByIdAsync(id);
        if (item is null || item.OwnerId != owner.Id)
            return NotFound();

        item.Title = request.Title;
        item.Description = request.Description;
        item.ContentText = request.ContentText;

        await _vaultItemRepository.UpdateAsync(item);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "VaultItemUpdated",
            "VaultItem", item.Id);

        return Ok(MapToDto(item));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = await _vaultItemRepository.GetByIdAsync(id);
        if (item is null || item.OwnerId != owner.Id)
            return NotFound();

        item.IsArchived = true;
        await _vaultItemRepository.UpdateAsync(item);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "VaultItemArchived",
            "VaultItem", item.Id);

        return NoContent();
    }

    [HttpPost("{id:guid}/recipients")]
    public async Task<IActionResult> AssignRecipient(Guid id, [FromBody] AssignRecipientRequest request)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = await _vaultItemRepository.GetWithRecipientsAsync(id);
        if (item is null || item.OwnerId != owner.Id)
            return NotFound();

        var alreadyAssigned = item.VaultItemRecipients.Any(vir => vir.RecipientId == request.RecipientId);
        if (alreadyAssigned)
            return Conflict(new ProblemDetails { Title = "Recipient is already assigned to this item." });

        var assignment = new VaultItemRecipient
        {
            VaultItemId = id,
            RecipientId = request.RecipientId,
            DeliveryPriority = request.DeliveryPriority,
            ScheduledDeliveryDelay = request.ScheduledDeliveryDelayMinutes.HasValue
                ? TimeSpan.FromMinutes(request.ScheduledDeliveryDelayMinutes.Value)
                : null
        };

        item.VaultItemRecipients.Add(assignment);
        await _vaultItemRepository.SaveChangesAsync();

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "RecipientAssignedToItem",
            "VaultItem", item.Id);

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, null);
    }

    [HttpDelete("{id:guid}/recipients/{recipientId:guid}")]
    public async Task<IActionResult> RemoveRecipient(Guid id, Guid recipientId)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = await _vaultItemRepository.GetWithRecipientsAsync(id);
        if (item is null || item.OwnerId != owner.Id)
            return NotFound();

        var assignment = item.VaultItemRecipients.FirstOrDefault(vir => vir.RecipientId == recipientId);
        if (assignment is null)
            return NotFound();

        item.VaultItemRecipients.Remove(assignment);
        await _vaultItemRepository.SaveChangesAsync();

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "RecipientRemovedFromItem",
            "VaultItem", item.Id);

        return NoContent();
    }

    private async Task<VaultOwner?> GetCurrentOwnerAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("sub");
        if (userId is null) return null;
        return await _vaultOwnerRepository.GetByIdentityUserIdAsync(userId);
    }

    private static VaultItemDto MapToDto(VaultItem item) => new(
        item.Id, item.OwnerId, item.Title, item.Description, item.ItemType,
        item.OriginalFileName, item.ContentType, item.FileSizeBytes, item.IsArchived,
        item.ExecutorId, item.LegalNotes, item.SpecialInstructions,
        item.VaultItemRecipients.Count, item.CreatedAt, item.UpdatedAt);
}
