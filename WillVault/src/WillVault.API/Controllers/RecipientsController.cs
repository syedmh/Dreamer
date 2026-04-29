using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WillVault.Application.DTOs.Recipients;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.API.Controllers;

[ApiController]
[Route("api/recipients")]
[Authorize]
public class RecipientsController : ControllerBase
{
    private readonly IRecipientRepository _recipientRepository;
    private readonly IVaultOwnerRepository _vaultOwnerRepository;
    private readonly IAuditService _auditService;

    public RecipientsController(
        IRecipientRepository recipientRepository,
        IVaultOwnerRepository vaultOwnerRepository,
        IAuditService auditService)
    {
        _recipientRepository = recipientRepository;
        _vaultOwnerRepository = vaultOwnerRepository;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var recipients = await _recipientRepository.GetByOwnerIdAsync(owner.Id);
        return Ok(recipients.Select(MapToDto));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var recipient = await _recipientRepository.GetByIdAsync(id);
        if (recipient is null || recipient.OwnerId != owner.Id)
            return NotFound();

        return Ok(MapToDto(recipient));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRecipientRequest request)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var recipient = new Recipient
        {
            OwnerId = owner.Id,
            FullName = request.FullName,
            Email = request.Email,
            Phone = request.Phone,
            Relationship = request.Relationship
        };

        await _recipientRepository.AddAsync(recipient);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "RecipientCreated",
            "Recipient", recipient.Id);

        return CreatedAtAction(nameof(GetById), new { id = recipient.Id }, MapToDto(recipient));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRecipientRequest request)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var recipient = await _recipientRepository.GetByIdAsync(id);
        if (recipient is null || recipient.OwnerId != owner.Id)
            return NotFound();

        recipient.FullName = request.FullName;
        recipient.Email = request.Email;
        recipient.Phone = request.Phone;
        recipient.Relationship = request.Relationship;

        await _recipientRepository.UpdateAsync(recipient);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "RecipientUpdated",
            "Recipient", recipient.Id);

        return Ok(MapToDto(recipient));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var owner = await GetCurrentOwnerAsync();
        if (owner is null) return Unauthorized();

        var recipient = await _recipientRepository.GetByIdAsync(id);
        if (recipient is null || recipient.OwnerId != owner.Id)
            return NotFound();

        await _recipientRepository.DeleteAsync(recipient);

        await _auditService.LogAsync(
            owner.Id, ActorType.Owner, "RecipientDeleted",
            "Recipient", recipient.Id);

        return NoContent();
    }

    [HttpPost("{id:guid}/verify")]
    public async Task<IActionResult> Verify(Guid id, [FromQuery] string token)
    {
        var recipient = await _recipientRepository.GetByIdAsync(id);
        if (recipient is null)
            return NotFound();

        if (recipient.EmailVerificationToken == token)
        {
            recipient.IsEmailVerified = true;
            recipient.EmailVerifiedAt = DateTime.UtcNow;
            recipient.EmailVerificationToken = null;
            await _recipientRepository.UpdateAsync(recipient);
            return Ok(new { message = "Email verified successfully." });
        }

        if (recipient.PhoneVerificationToken == token)
        {
            recipient.IsPhoneVerified = true;
            recipient.PhoneVerifiedAt = DateTime.UtcNow;
            recipient.PhoneVerificationToken = null;
            await _recipientRepository.UpdateAsync(recipient);
            return Ok(new { message = "Phone verified successfully." });
        }

        return BadRequest(new ProblemDetails { Title = "Invalid verification token." });
    }

    private async Task<VaultOwner?> GetCurrentOwnerAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("sub");
        if (userId is null) return null;
        return await _vaultOwnerRepository.GetByIdentityUserIdAsync(userId);
    }

    private static RecipientDto MapToDto(Recipient r) => new(
        r.Id, r.OwnerId, r.FullName, r.Email, r.Phone, r.Relationship,
        r.IsEmailVerified, r.IsPhoneVerified, r.EmailVerifiedAt, r.PhoneVerifiedAt,
        r.CreatedAt, r.UpdatedAt);
}
