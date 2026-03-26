namespace Khilat.Shared.DTOs.Auth;

public class AuthResponse
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public string? Role { get; set; }
    public string? ErrorMessage { get; set; }
}
