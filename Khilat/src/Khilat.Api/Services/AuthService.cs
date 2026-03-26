namespace Khilat.Api.Services;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Khilat.Infrastructure.Identity;
using Microsoft.IdentityModel.Tokens;

public interface IAuthService
{
    string GenerateJwtToken(ApplicationUser user, string role, Guid? customerId);
    string GenerateRefreshToken();
}

public class AuthService : IAuthService
{
    private readonly IConfiguration _configuration;

    public AuthService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GenerateJwtToken(ApplicationUser user, string role, Guid? customerId)
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["Key"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
            new("UserId", user.Id),
            new("Email", user.Email!)
        };

        if (customerId.HasValue)
            claims.Add(new Claim("CustomerId", customerId.Value.ToString()));

        var expiryMinutes = int.Parse(jwtSettings["ExpiryMinutes"] ?? "60", System.Globalization.CultureInfo.InvariantCulture);

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }
}
