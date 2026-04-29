using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WillVault.Application.DTOs.Common;
using WillVault.Application.DTOs.Wills;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.API.Controllers;

[ApiController]
[Route("api/wills")]
[Authorize]
public class WillsController : ControllerBase
{
    private readonly IVaultItemRepository _vaultItemRepository;
    private readonly IVaultOwnerRepository _vaultOwnerRepository;
    private readonly IStorageProvider _storageProvider;
    private readonly IAuditService _auditService;

    public WillsController(
        IVaultItemRepository vaultItemRepository,
        IVaultOwnerRepository vaultOwnerRepository,
        IStorageProvider storageProvider,
        IAuditService auditService)
    {
        _vaultItemRepository = vaultItemRepository;
        _vaultOwnerRepository = vaultOwnerRepository;
        _storageProvider = storageProvider;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var items = await _vaultItemRepository.GetByOwnerIdAndTypeAsync(owner.Id, VaultItemType.Will);
        var dtos = items.Where(i => !i.IsArchived).Select(MapToDto).ToList();
        return Ok(dtos);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = await _vaultItemRepository.GetWithRecipientsAsync(id);
        if (item is null || item.OwnerId != owner.Id || item.ItemType != VaultItemType.Will)
            return NotFound();

        return Ok(MapToDto(item));
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        IFormFile file,
        [FromForm] string title,
        [FromForm] string? description,
        [FromForm] Guid? executorId,
        [FromForm] string? legalNotes,
        [FromForm] string? specialInstructions)
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
            ItemType = VaultItemType.Will,
            ContentPath = storagePath,
            OriginalFileName = file.FileName,
            ContentType = file.ContentType,
            FileSizeBytes = file.Length,
            ExecutorId = executorId,
            LegalNotes = legalNotes,
            SpecialInstructions = specialInstructions
        };

        await _vaultItemRepository.AddAsync(item);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "WillCreated",
            "VaultItem", item.Id);

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, MapToDto(item));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateWillRequest request)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = await _vaultItemRepository.GetByIdAsync(id);
        if (item is null || item.OwnerId != owner.Id || item.ItemType != VaultItemType.Will)
            return NotFound();

        item.Title = request.Title;
        item.Description = request.Description;
        item.ExecutorId = request.ExecutorId;
        item.LegalNotes = request.LegalNotes;
        item.SpecialInstructions = request.SpecialInstructions;

        await _vaultItemRepository.UpdateAsync(item);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "WillUpdated",
            "VaultItem", item.Id);

        return Ok(MapToDto(item));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = await _vaultItemRepository.GetByIdAsync(id);
        if (item is null || item.OwnerId != owner.Id || item.ItemType != VaultItemType.Will)
            return NotFound();

        item.IsArchived = true;
        await _vaultItemRepository.UpdateAsync(item);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "WillArchived",
            "VaultItem", item.Id);

        return NoContent();
    }

    [HttpPost("{id:guid}/executor")]
    public async Task<IActionResult> DesignateExecutor(Guid id, [FromBody] Guid executorId)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var item = await _vaultItemRepository.GetByIdAsync(id);
        if (item is null || item.OwnerId != owner.Id || item.ItemType != VaultItemType.Will)
            return NotFound();

        item.ExecutorId = executorId;
        await _vaultItemRepository.UpdateAsync(item);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "ExecutorDesignated",
            "VaultItem", item.Id,
            $"{{\"executorId\":\"{executorId}\"}}");

        return Ok(MapToDto(item));
    }

    private async Task<VaultOwner?> GetCurrentOwnerAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("sub");
        if (userId is null) return null;
        return await _vaultOwnerRepository.GetByIdentityUserIdAsync(userId);
    }

    private static WillDto MapToDto(VaultItem item) => new(
        item.Id, item.OwnerId, item.Title, item.Description,
        item.ExecutorId, item.LegalNotes, item.SpecialInstructions,
        item.IsArchived, item.VaultItemRecipients.Count,
        item.CreatedAt, item.UpdatedAt);
}
