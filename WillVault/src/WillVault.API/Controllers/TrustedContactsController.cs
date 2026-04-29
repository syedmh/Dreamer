using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.API.Controllers;

[ApiController]
[Route("api/trusted-contacts")]
[Authorize]
public class TrustedContactsController : ControllerBase
{
    private readonly ITrustedContactRepository _trustedContactRepository;
    private readonly IVaultOwnerRepository _vaultOwnerRepository;
    private readonly IAuditService _auditService;

    public TrustedContactsController(
        ITrustedContactRepository trustedContactRepository,
        IVaultOwnerRepository vaultOwnerRepository,
        IAuditService auditService)
    {
        _trustedContactRepository = trustedContactRepository;
        _vaultOwnerRepository = vaultOwnerRepository;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var contacts = await _trustedContactRepository.GetByOwnerIdAsync(owner.Id);
        return Ok(contacts.Select(MapToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var contact = await _trustedContactRepository.GetByIdAsync(id);
        if (contact is null || contact.OwnerId != owner.Id)
            return NotFound();

        return Ok(MapToDto(contact));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTrustedContactRequest request)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var contact = new TrustedContact
        {
            OwnerId = owner.Id,
            FullName = request.FullName,
            Email = request.Email,
            Phone = request.Phone,
            Relationship = request.Relationship,
            VerificationToken = Guid.NewGuid().ToString("N")
        };

        await _trustedContactRepository.AddAsync(contact);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "TrustedContactCreated",
            "TrustedContact", contact.Id);

        return CreatedAtAction(nameof(GetById), new { id = contact.Id }, MapToDto(contact));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTrustedContactRequest request)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var contact = await _trustedContactRepository.GetByIdAsync(id);
        if (contact is null || contact.OwnerId != owner.Id)
            return NotFound();

        contact.FullName = request.FullName;
        contact.Email = request.Email;
        contact.Phone = request.Phone;
        contact.Relationship = request.Relationship;

        await _trustedContactRepository.UpdateAsync(contact);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "TrustedContactUpdated",
            "TrustedContact", contact.Id);

        return Ok(MapToDto(contact));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var contact = await _trustedContactRepository.GetByIdAsync(id);
        if (contact is null || contact.OwnerId != owner.Id)
            return NotFound();

        await _trustedContactRepository.DeleteAsync(contact);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "TrustedContactDeleted",
            "TrustedContact", contact.Id);

        return NoContent();
    }

    private async Task<VaultOwner?> GetCurrentOwnerAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("sub");
        if (userId is null) return null;
        return await _vaultOwnerRepository.GetByIdentityUserIdAsync(userId);
    }

    private static TrustedContactDto MapToDto(TrustedContact c) => new(
        c.Id, c.OwnerId, c.FullName, c.Email, c.Phone, c.Relationship,
        c.IsVerified, c.VerifiedAt, c.CreatedAt, c.UpdatedAt);
}

public record TrustedContactDto(
    Guid Id, Guid OwnerId, string FullName, string Email,
    string? Phone, string? Relationship, bool IsVerified,
    DateTime? VerifiedAt, DateTime CreatedAt, DateTime UpdatedAt);

public record CreateTrustedContactRequest(
    string FullName, string Email, string? Phone, string? Relationship);

public record UpdateTrustedContactRequest(
    string FullName, string Email, string? Phone, string? Relationship);
