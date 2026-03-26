namespace Khilat.Api.Controllers;

using Khilat.Api.Services;
using Khilat.Core.Entities;
using Khilat.Core.Interfaces;
using Khilat.Infrastructure.Identity;
using Khilat.Shared.DTOs.Auth;
using Khilat.Shared.DTOs.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IAuthService _authService;
    private readonly ICustomerRepository _customerRepository;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IAuthService authService,
        ICustomerRepository customerRepository)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _authService = authService;
        _customerRepository = customerRepository;
    }

    [HttpPost("register")]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Register([FromBody] RegisterRequest request)
    {
        if (request.Password != request.ConfirmPassword)
            return BadRequest(ApiResponse<AuthResponse>.Fail("Passwords do not match."));

        var existingUser = await _userManager.FindByEmailAsync(request.Email);
        if (existingUser is not null)
            return BadRequest(ApiResponse<AuthResponse>.Fail("An account with this email already exists."));

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            Phone = request.Phone ?? string.Empty
        };

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName,
            CustomerId = customer.Id
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            var errors = result.Errors.Select(e => e.Description).ToList();
            return BadRequest(ApiResponse<AuthResponse>.Fail(errors));
        }

        await _userManager.AddToRoleAsync(user, "Customer");
        customer.UserId = user.Id;
        await _customerRepository.AddAsync(customer);

        var token = _authService.GenerateJwtToken(user, "Customer", customer.Id);
        var refreshToken = _authService.GenerateRefreshToken();

        var response = new AuthResponse
        {
            Success = true,
            Token = token,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Email = user.Email,
            FullName = $"{user.FirstName} {user.LastName}",
            Role = "Customer"
        };

        return Ok(ApiResponse<AuthResponse>.Ok(response));
    }

    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Login([FromBody] LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return Unauthorized(ApiResponse<AuthResponse>.Fail("Invalid email or password."));

        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: false);
        if (!result.Succeeded)
            return Unauthorized(ApiResponse<AuthResponse>.Fail("Invalid email or password."));

        var roles = await _userManager.GetRolesAsync(user);
        var role = roles.FirstOrDefault() ?? "Customer";

        var token = _authService.GenerateJwtToken(user, role, user.CustomerId);
        var refreshToken = _authService.GenerateRefreshToken();

        var response = new AuthResponse
        {
            Success = true,
            Token = token,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Email = user.Email,
            FullName = $"{user.FirstName} {user.LastName}",
            Role = role
        };

        return Ok(ApiResponse<AuthResponse>.Ok(response));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> Refresh([FromBody] RefreshTokenRequest request)
    {
        // In production, validate the refresh token against a store
        var principal = GetPrincipalFromExpiredToken(request.Token);
        if (principal is null)
            return Unauthorized(ApiResponse<AuthResponse>.Fail("Invalid token."));

        var userId = principal.FindFirst("UserId")?.Value;
        if (userId is null)
            return Unauthorized(ApiResponse<AuthResponse>.Fail("Invalid token."));

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return Unauthorized(ApiResponse<AuthResponse>.Fail("User not found."));

        var roles = await _userManager.GetRolesAsync(user);
        var role = roles.FirstOrDefault() ?? "Customer";

        var token = _authService.GenerateJwtToken(user, role, user.CustomerId);
        var refreshToken = _authService.GenerateRefreshToken();

        var response = new AuthResponse
        {
            Success = true,
            Token = token,
            RefreshToken = refreshToken,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Email = user.Email,
            FullName = $"{user.FirstName} {user.LastName}",
            Role = role
        };

        return Ok(ApiResponse<AuthResponse>.Ok(response));
    }

    [HttpGet("profile")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<CustomerProfileDto>>> GetProfile()
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<CustomerProfileDto>.Fail("Customer not found."));

        var customer = await _customerRepository.GetByIdAsync(customerId.Value);
        if (customer is null)
            return NotFound(ApiResponse<CustomerProfileDto>.Fail("Customer profile not found."));

        var dto = new CustomerProfileDto
        {
            Id = customer.Id,
            FirstName = customer.FirstName,
            LastName = customer.LastName,
            Email = customer.Email,
            Phone = customer.Phone,
            Addresses = customer.Addresses.Select(a => new AddressDto
            {
                Id = a.Id,
                Street = a.Street,
                City = a.City,
                State = a.State,
                ZipCode = a.ZipCode,
                Country = a.Country,
                IsDefault = a.IsDefault
            }).ToList()
        };

        return Ok(ApiResponse<CustomerProfileDto>.Ok(dto));
    }

    [HttpPut("profile")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<CustomerProfileDto>>> UpdateProfile([FromBody] CustomerProfileDto request)
    {
        var customerId = GetCustomerId();
        if (customerId is null)
            return Unauthorized(ApiResponse<CustomerProfileDto>.Fail("Customer not found."));

        var customer = await _customerRepository.GetByIdAsync(customerId.Value);
        if (customer is null)
            return NotFound(ApiResponse<CustomerProfileDto>.Fail("Customer profile not found."));

        customer.FirstName = request.FirstName;
        customer.LastName = request.LastName;
        customer.Phone = request.Phone;
        customer.UpdatedAt = DateTime.UtcNow;

        await _customerRepository.UpdateAsync(customer);

        var dto = new CustomerProfileDto
        {
            Id = customer.Id,
            FirstName = customer.FirstName,
            LastName = customer.LastName,
            Email = customer.Email,
            Phone = customer.Phone,
            Addresses = customer.Addresses.Select(a => new AddressDto
            {
                Id = a.Id,
                Street = a.Street,
                City = a.City,
                State = a.State,
                ZipCode = a.ZipCode,
                Country = a.Country,
                IsDefault = a.IsDefault
            }).ToList()
        };

        return Ok(ApiResponse<CustomerProfileDto>.Ok(dto));
    }

    private Guid? GetCustomerId()
    {
        var claim = User.FindFirst("CustomerId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private System.Security.Claims.ClaimsPrincipal? GetPrincipalFromExpiredToken(string token)
    {
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var jwtSettings = configuration.GetSection("JwtSettings");

        var tokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(jwtSettings["Key"]!)),
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            ValidateLifetime = false // Allow expired tokens for refresh
        };

        var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        try
        {
            var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);
            if (securityToken is not System.IdentityModel.Tokens.Jwt.JwtSecurityToken jwtSecurityToken
                || !jwtSecurityToken.Header.Alg.Equals(
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return principal;
        }
        catch
        {
            return null;
        }
    }
}
