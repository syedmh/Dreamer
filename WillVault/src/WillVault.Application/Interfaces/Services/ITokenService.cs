using System.Security.Claims;

namespace WillVault.Application.Interfaces.Services;

/// <summary>
/// Abstraction for JWT access token and refresh token operations.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Generates a JWT access token for the specified user.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <param name="email">The user's email address.</param>
    /// <param name="roles">The roles assigned to the user.</param>
    /// <returns>A signed JWT access token string.</returns>
    string GenerateAccessToken(string userId, string email, IEnumerable<string> roles);

    /// <summary>
    /// Generates a cryptographically secure refresh token.
    /// </summary>
    /// <returns>A refresh token string.</returns>
    string GenerateRefreshToken();

    /// <summary>
    /// Extracts the claims principal from an expired access token for token refresh scenarios.
    /// </summary>
    /// <param name="token">The expired JWT access token.</param>
    /// <returns>The <see cref="ClaimsPrincipal"/> extracted from the token.</returns>
    ClaimsPrincipal GetPrincipalFromExpiredToken(string token);
}
