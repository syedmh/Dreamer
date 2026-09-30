using System.Net.Http.Json;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Husaynia.Infrastructure.Persistence.Core;
using Husaynia.Web.Areas.Admin.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Husaynia.IntegrationTests.Identity;

internal sealed class IdentitySqlServerTestDatabase : IAsyncDisposable
{
    private static int sequence;
    private readonly string databaseName;
    private bool disposed;

    private IdentitySqlServerTestDatabase(string databaseName, string connectionString)
    {
        this.databaseName = databaseName;
        ConnectionString = connectionString;
    }

    internal string ConnectionString { get; }

    internal static async Task<IdentitySqlServerTestDatabase> CreateAsync(
        string testName,
        CancellationToken cancellationToken = default)
    {
        var number = Interlocked.Increment(ref sequence);
        var databaseName = DatabaseName.Create(
            "HusayniaT04",
            $"{testName}_{Environment.ProcessId}_{number}");
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 30,
        }.ConnectionString;

        var database = new IdentitySqlServerTestDatabase(databaseName, connectionString);
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await database.InstallInvariantsAsync(cancellationToken).ConfigureAwait(false);
        return database;
    }

    internal async Task InstallInvariantsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
                IdentityAuditDatabaseInvariant.InstallSql,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task DownInvariantsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
                IdentityAuditDatabaseInvariant.DownSql,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task ReinstallInvariantsAsync(CancellationToken cancellationToken = default)
    {
        await DownInvariantsAsync(cancellationToken).ConfigureAwait(false);
        await InstallInvariantsAsync(cancellationToken).ConfigureAwait(false);
    }

    internal HusayniaIdentityDbContext CreateContext()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var options = new DbContextOptionsBuilder<HusayniaIdentityDbContext>()
            .UseSqlServer(ConnectionString)
            .EnableDetailedErrors()
            .Options;
        return new HusayniaIdentityDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        var options = new DbContextOptionsBuilder<HusayniaIdentityDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        disposed = true;
        await using var context = new HusayniaIdentityDbContext(options);
        var builder = new SqlConnectionStringBuilder(context.Database.GetConnectionString());
        if (!builder.InitialCatalog.Equals(databaseName, StringComparison.Ordinal) ||
            !databaseName.StartsWith("HusayniaT04_", StringComparison.Ordinal) ||
            !builder.DataSource.Equals(@"(localdb)\MSSQLLocalDB", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to delete a database that is not a disposable T04 LocalDB database.");
        }

        await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        SqlConnection.ClearAllPools();
    }
}

internal sealed class IdentityWebApplicationFactory : WebApplicationFactory<IdentityWebAssemblyMarker>
{
    private static readonly object EnvironmentGate = new();
    private static readonly string AnonymousRateLimitFingerprintKey =
        Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
    private readonly IdentitySqlServerTestDatabase database;
    private readonly IReadOnlyDictionary<string, string?> extraConfiguration;
    private readonly string environmentName;
    private readonly Action<IServiceCollection>? configureServices;
    private readonly Dictionary<string, string?> priorEnvironment = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> effectiveConfiguration = new(StringComparer.Ordinal);

    internal IdentityWebApplicationFactory(
        IdentitySqlServerTestDatabase database,
        IReadOnlyDictionary<string, string?>? extraConfiguration = null,
        string environmentName = "IntegrationTesting",
        Action<IServiceCollection>? configureServices = null)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.extraConfiguration = extraConfiguration ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        this.environmentName = environmentName ?? throw new ArgumentNullException(nameof(environmentName));
        this.configureServices = configureServices;

        effectiveConfiguration["ConnectionStrings:HusayniaDatabase"] = this.database.ConnectionString;
        effectiveConfiguration["Identity:Bootstrap:Enabled"] = "false";
        effectiveConfiguration["Identity:InvitationLifetime"] = "7.00:00:00";
        effectiveConfiguration["Identity:AnonymousRateLimit:PermitLimit"] = "1000";
        effectiveConfiguration["Identity:AnonymousRateLimit:Window"] = "00:05:00";
        effectiveConfiguration["Identity:AnonymousRateLimit:Retention"] = "1.00:00:00";
        effectiveConfiguration["Identity:AnonymousRateLimit:FingerprintKey"] =
            AnonymousRateLimitFingerprintKey;
        foreach (var pair in this.extraConfiguration)
        {
            effectiveConfiguration[pair.Key] = pair.Value;
        }

        ApplyEnvironmentVariables();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment(environmentName);
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(effectiveConfiguration);
        });
        if (configureServices is not null)
        {
            builder.ConfigureServices(configureServices);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        RestoreEnvironmentVariables();
    }

    internal async Task EnsureFrozenRolesAsync()
    {
        using var scope = Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<HusayniaIdentityRole>>();
        foreach (var roleName in RoleNames.All.Order(StringComparer.Ordinal))
        {
            if (await roleManager.RoleExistsAsync(roleName).ConfigureAwait(false))
            {
                continue;
            }

            var result = await roleManager.CreateAsync(new HusayniaIdentityRole(roleName))
                .ConfigureAwait(false);
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }

    internal async Task<SeededIdentityUser> SeedUserAsync(
        string email,
        string password,
        IReadOnlyCollection<string> roles,
        bool enableMfa,
        bool confirmed = true,
        bool disabled = false)
    {
        await EnsureFrozenRolesAsync().ConfigureAwait(false);

        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();
        var user = new HusayniaIdentityUser(email)
        {
            EmailConfirmed = confirmed,
        };
        var createResult = await userManager.CreateAsync(user, password).ConfigureAwait(false);
        Assert.True(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(error => error.Code)));

        if (roles.Count > 0)
        {
            var addRoles = await userManager.AddToRolesAsync(user, roles).ConfigureAwait(false);
            Assert.True(addRoles.Succeeded, string.Join(", ", addRoles.Errors.Select(error => error.Code)));
        }

        string? authenticatorKey = null;
        if (enableMfa)
        {
            var resetKey = await userManager.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
            Assert.True(resetKey.Succeeded, string.Join(", ", resetKey.Errors.Select(error => error.Code)));
            authenticatorKey = await userManager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
            var enableResult = await userManager.SetTwoFactorEnabledAsync(user, true).ConfigureAwait(false);
            Assert.True(enableResult.Succeeded, string.Join(", ", enableResult.Errors.Select(error => error.Code)));
        }

        if (disabled)
        {
            user.Disable(DateTimeOffset.UtcNow);
            var update = await userManager.UpdateAsync(user).ConfigureAwait(false);
            Assert.True(update.Succeeded, string.Join(", ", update.Errors.Select(error => error.Code)));
            var stamp = await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
            Assert.True(stamp.Succeeded, string.Join(", ", stamp.Errors.Select(error => error.Code)));
        }

        var refreshed = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
        Assert.NotNull(refreshed);
        return new SeededIdentityUser(
            refreshed!.Id.ToString(),
            refreshed.Email ?? email,
            password,
            refreshed.ConcurrencyStamp ?? string.Empty,
            authenticatorKey);
    }

    internal async Task<IReadOnlyList<AuditEvent>> ReadAuditEventsAsync()
    {
        await using var context = database.CreateContext();
        return await context.Set<AuditEvent>()
            .AsNoTracking()
            .OrderBy(entry => entry.OccurredAtUtc)
            .ThenBy(entry => entry.Id)
            .ToArrayAsync()
            .ConfigureAwait(false);
    }

    internal async Task<HusayniaIdentityUser?> FindUserAsync(string email)
    {
        await using var context = database.CreateContext();
        return await context.Set<HusayniaIdentityUser>()
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Email == email)
            .ConfigureAwait(false);
    }

    internal async Task<string?> ReadAuthenticatorKeyAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();
        var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false);
        Assert.NotNull(user);
        return await userManager.GetAuthenticatorKeyAsync(user!).ConfigureAwait(false);
    }

    internal async Task<HttpClient> CreateAuthenticatedClientAsync(
        SeededIdentityUser seededUser,
        IReadOnlyCollection<string> roles,
        bool mfaSatisfied)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();
        var user = await userManager.FindByIdAsync(seededUser.UserId).ConfigureAwait(false);
        Assert.NotNull(user);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, seededUser.UserId),
            new(ClaimTypes.Name, user!.UserName ?? seededUser.Email),
            new("husaynia.security_stamp", user.SecurityStamp ?? string.Empty),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        if (mfaSatisfied)
        {
            claims.Add(new Claim("amr", "mfa"));
            claims.Add(new Claim("husaynia.mfa", "true"));
        }

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme));
        var options = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(
            principal,
            IdentityConstants.ApplicationScheme);
        var protectedTicket = options.TicketDataFormat.Protect(ticket);
        var client = this.CreateIdentityClient();
        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{options.Cookie.Name}={protectedTicket}");
        return client;
    }

    internal async Task LockOutUserAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();
        var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false);
        Assert.NotNull(user);
        var result = await userManager.SetLockoutEndDateAsync(
                user!,
                DateTimeOffset.UtcNow.AddMinutes(15))
            .ConfigureAwait(false);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));
    }

    internal async Task SeedInvitationAsync(
        string email,
        string rawToken,
        DateTimeOffset expiresAtUtc,
        bool disabled = false)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();
        var user = new HusayniaIdentityUser(email);
        var create = await userManager.CreateAsync(user).ConfigureAwait(false);
        Assert.True(create.Succeeded, string.Join(", ", create.Errors.Select(error => error.Code)));
        user.IssueInvitation(
            HusayniaIdentityToken.Hash(rawToken),
            DateTimeOffset.UtcNow.AddDays(-1),
            expiresAtUtc);
        if (disabled)
        {
            user.Disable(DateTimeOffset.UtcNow);
        }

        var update = await userManager.UpdateAsync(user).ConfigureAwait(false);
        Assert.True(update.Succeeded, string.Join(", ", update.Errors.Select(error => error.Code)));
    }

    internal async Task AddRoleAsync(string userId, string role)
    {
        await EnsureFrozenRolesAsync().ConfigureAwait(false);
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();
        var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false);
        Assert.NotNull(user);
        var result = await userManager.AddToRoleAsync(user!, role).ConfigureAwait(false);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));
    }

    internal async Task<IdentityUserView> ReadUserViewAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();
        var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false);
        Assert.NotNull(user);
        var roles = await userManager.GetRolesAsync(user!).ConfigureAwait(false);
        return new IdentityUserView(
            userId,
            user!.Email ?? string.Empty,
            user.IsDisabled,
            user.TwoFactorEnabled,
            !string.IsNullOrWhiteSpace(user.PasswordHash),
            user.ConcurrencyStamp ?? string.Empty,
            roles.Order(StringComparer.Ordinal).ToArray());
    }

    private void ApplyEnvironmentVariables()
    {
        lock (EnvironmentGate)
        {
            foreach (var pair in effectiveConfiguration)
            {
                var variableName = pair.Key.Replace(":", "__", StringComparison.Ordinal);
                priorEnvironment[variableName] = Environment.GetEnvironmentVariable(variableName);
                Environment.SetEnvironmentVariable(variableName, pair.Value);
            }
        }
    }

    private void RestoreEnvironmentVariables()
    {
        lock (EnvironmentGate)
        {
            foreach (var pair in priorEnvironment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            priorEnvironment.Clear();
        }
    }
}

internal sealed record SeededIdentityUser(
    string UserId,
    string Email,
    string Password,
    string ConcurrencyStamp,
    string? AuthenticatorKey);

internal static class IdentityHttpClientExtensions
{
    internal static HttpClient CreateIdentityClient(this IdentityWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    internal static async Task<string> GetAntiforgeryTokenAsync(this HttpClient client)
    {
        var response = await client.GetFromJsonAsync<AntiforgeryTokenDto>("/admin/identity/antiforgery");
        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response!.RequestToken));
        return response.RequestToken;
    }

    internal static async Task<HttpResponseMessage> PostJsonWithAntiforgeryAsync(
        this HttpClient client,
        string requestUri,
        object body,
        string antiforgeryToken,
        string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("RequestVerificationToken", antiforgeryToken);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Add("X-Correlation-ID", correlationId);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    internal static async Task<HttpResponseMessage> PutJsonWithAntiforgeryAsync(
        this HttpClient client,
        string requestUri,
        object body,
        string antiforgeryToken,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("RequestVerificationToken", antiforgeryToken);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Add("X-Correlation-ID", correlationId);
        }

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<LoginResponseDto> LoginAsync(
        this HttpClient client,
        string email,
        string password,
        string? oneTimeCode = null)
    {
        var antiforgeryToken = await client.GetAntiforgeryTokenAsync().ConfigureAwait(false);
        var response = await client.PostJsonWithAntiforgeryAsync(
                "/admin/identity/login",
                new { email, password, oneTimeCode },
                antiforgeryToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<LoginResponseDto>().ConfigureAwait(false);
        Assert.NotNull(payload);
        return payload!;
    }

    internal static async Task<FailureResponseDto?> ReadFailureAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<FailureResponseDto>().ConfigureAwait(false);

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "ASP.NET Core Identity authenticator tokens follow the TOTP/HMAC-SHA1 standard.")]
    internal static string CreateTotpCode(string authenticatorKey, DateTimeOffset? timestamp = null)
    {
        var secret = DecodeBase32(authenticatorKey);
        var unixSeconds = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var timestep = unixSeconds / 30;
        Span<byte> counterBytes = stackalloc byte[8];
        for (var index = 7; index >= 0; index--)
        {
            counterBytes[index] = (byte)(timestep & 0xFF);
            timestep >>= 8;
        }

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        var code = binary % 1_000_000;
        return code.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] DecodeBase32(string value)
    {
        var normalized = new string([.. value
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=')
            .ToUpperInvariant()]);
        var buffer = new List<byte>(normalized.Length * 5 / 8);
        var bits = 0;
        var bitBuffer = 0;

        foreach (var character in normalized)
        {
            var current = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => throw new InvalidOperationException("The authenticator key contains invalid base32 data."),
            };
            bitBuffer = (bitBuffer << 5) | current;
            bits += 5;
            while (bits >= 8)
            {
                bits -= 8;
                buffer.Add((byte)((bitBuffer >> bits) & 0xFF));
            }
        }

        return [.. buffer];
    }
}

internal sealed record AntiforgeryTokenDto(string RequestToken);

internal sealed record FailureResponseDto(string Code, string Message, string CorrelationId);

internal sealed record SessionViewDto(
    string UserId,
    string Email,
    IReadOnlyCollection<string> Roles,
    bool MfaSatisfied,
    bool TwoFactorEnabled,
    bool RequiresMfaEnrollment);

internal sealed record LoginResponseDto(SessionViewDto Session, string Status);

internal sealed record InvitationAcceptedResponseDto(string UserId, string Email);

internal sealed record MfaSetupResponseDto(string SharedKey, bool AlreadyEnabled);
