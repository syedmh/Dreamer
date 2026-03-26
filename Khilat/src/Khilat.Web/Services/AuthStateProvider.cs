namespace Khilat.Web.Services;

using System.Security.Claims;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;

public class AuthStateProvider : AuthenticationStateProvider
{
    private readonly ILocalStorageService _localStorage;

    public AuthStateProvider(ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await _localStorage.GetItemAsStringAsync("authToken");
        if (string.IsNullOrEmpty(token))
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

        var claims = ParseClaimsFromJwt(token.Trim('"'));
        var identity = new ClaimsIdentity(claims, "jwt");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public void NotifyAuthStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public ILocalStorageService LocalStorage => _localStorage;

    private static IEnumerable<Claim> ParseClaimsFromJwt(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var jsonBytes = Convert.FromBase64String(PadBase64(payload));
        var kvp = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonBytes);
        return kvp?.Select(k => new Claim(k.Key, k.Value.ToString() ?? "")) ?? Enumerable.Empty<Claim>();
    }

    private static string PadBase64(string base64)
    {
        switch (base64.Length % 4)
        {
            case 2: return base64 + "==";
            case 3: return base64 + "=";
            default: return base64;
        }
    }
}
