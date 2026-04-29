using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using WillVault.Application.DTOs.Auth;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;
using WillVault.Infrastructure.Identity;

namespace WillVault.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly IVaultOwnerRepository _vaultOwnerRepository;
    private readonly IAuditService _auditService;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        IVaultOwnerRepository vaultOwnerRepository,
        IAuditService auditService)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _vaultOwnerRepository = vaultOwnerRepository;
        _auditService = auditService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (request.Password != request.ConfirmPassword)
            return BadRequest(new ProblemDetails { Title = "Passwords do not match." });

        var existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser is not null)
            return Conflict(new ProblemDetails { Title = "A user with this email already exists." });

        var identityUser = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email
        };

        var createResult = await _userManager.CreateAsync(identityUser, request.Password);
        if (!createResult.Succeeded)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Registration failed.",
                Detail = string.Join("; ", createResult.Errors.Select(e => e.Description))
            });
        }

        var vaultOwner = new VaultOwner
        {
            FullName = request.FullName,
            Email = request.Email,
            Phone = request.Phone,
            DateOfBirth = request.DateOfBirth,
            IdentityUserId = identityUser.Id,
            AccountStatus = AccountStatus.Active
        };

        await _vaultOwnerRepository.AddAsync(vaultOwner);

        identityUser.VaultOwnerId = vaultOwner.Id;
        await _userManager.UpdateAsync(identityUser);

        var roles = await _userManager.GetRolesAsync(identityUser);
        var accessToken = _tokenService.GenerateAccessToken(identityUser.Id, identityUser.Email!, roles);
        var refreshToken = _tokenService.GenerateRefreshToken();

        identityUser.RefreshToken = refreshToken;
        identityUser.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
        await _userManager.UpdateAsync(identityUser);

        await _auditService.LogAsync(
            vaultOwner.Id, ActorType.Owner, "UserRegistered",
            "VaultOwner", vaultOwner.Id);

        var expirationMinutes = int.TryParse(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Jwt:ExpirationMinutes"],
            out var exp) ? exp : 15;

        return CreatedAtAction(nameof(Register), new AuthResponse(
            accessToken,
            refreshToken,
            DateTime.UtcNow.AddMinutes(expirationMinutes),
            new UserDto(vaultOwner.Id, vaultOwner.FullName, vaultOwner.Email,
                vaultOwner.Phone, vaultOwner.AccountStatus)));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return Unauthorized(new ProblemDetails { Title = "Invalid email or password." });

        var validPassword = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!validPassword)
            return Unauthorized(new ProblemDetails { Title = "Invalid email or password." });

        var vaultOwner = await _vaultOwnerRepository.GetByIdentityUserIdAsync(user.Id);
        if (vaultOwner is null)
            return Unauthorized(new ProblemDetails { Title = "User profile not found." });

        var roles = await _userManager.GetRolesAsync(user);
        var accessToken = _tokenService.GenerateAccessToken(user.Id, user.Email!, roles);
        var refreshToken = _tokenService.GenerateRefreshToken();

        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
        await _userManager.UpdateAsync(user);

        await _auditService.LogAsync(
            vaultOwner.Id, ActorType.Owner, "UserLoggedIn",
            "VaultOwner", vaultOwner.Id);

        var expirationMinutes = int.TryParse(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Jwt:ExpirationMinutes"],
            out var exp) ? exp : 15;

        return Ok(new AuthResponse(
            accessToken,
            refreshToken,
            DateTime.UtcNow.AddMinutes(expirationMinutes),
            new UserDto(vaultOwner.Id, vaultOwner.FullName, vaultOwner.Email,
                vaultOwner.Phone, vaultOwner.AccountStatus)));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request)
    {
        var principal = _tokenService.GetPrincipalFromExpiredToken(request.AccessToken);
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? principal.FindFirstValue("sub");
        if (userId is null)
            return Unauthorized(new ProblemDetails { Title = "Invalid token." });

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null ||
            user.RefreshToken != request.RefreshToken ||
            user.RefreshTokenExpiryTime <= DateTime.UtcNow)
        {
            return Unauthorized(new ProblemDetails { Title = "Invalid or expired refresh token." });
        }

        var vaultOwner = await _vaultOwnerRepository.GetByIdentityUserIdAsync(user.Id);
        if (vaultOwner is null)
            return Unauthorized(new ProblemDetails { Title = "User profile not found." });

        var roles = await _userManager.GetRolesAsync(user);
        var newAccessToken = _tokenService.GenerateAccessToken(user.Id, user.Email!, roles);
        var newRefreshToken = _tokenService.GenerateRefreshToken();

        user.RefreshToken = newRefreshToken;
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
        await _userManager.UpdateAsync(user);

        var expirationMinutes = int.TryParse(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Jwt:ExpirationMinutes"],
            out var exp) ? exp : 15;

        return Ok(new AuthResponse(
            newAccessToken,
            newRefreshToken,
            DateTime.UtcNow.AddMinutes(expirationMinutes),
            new UserDto(vaultOwner.Id, vaultOwner.FullName, vaultOwner.Email,
                vaultOwner.Phone, vaultOwner.AccountStatus)));
    }
}

public record RefreshTokenRequest(string AccessToken, string RefreshToken);
