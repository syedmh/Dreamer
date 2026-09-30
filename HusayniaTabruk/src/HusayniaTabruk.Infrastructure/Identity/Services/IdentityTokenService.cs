using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed class IdentityTokenService(
    TabrukDbContext context,
    IClock clock,
    IAccessTokenCodec accessTokenCodec,
    Microsoft.Extensions.Options.IOptions<TabrukAuthOptions> options)
    : ITokenService
{
    private const string RefreshLoginProvider = "tabruk.refresh.v1";
    private const string RefreshConsumedLoginProvider = "tabruk.refresh.consumed.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<TokenPair> IssueAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => IssueCoreAsync(userId, membershipId, organizationId, deviceId, cancellationToken));

    public ValueTask<Result<TokenPair>> RotateAsync(
        string refreshToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => RotateCoreAsync(refreshToken, deviceId, cancellationToken));

    public ValueTask RevokeAsync(
        string refreshToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default) =>
        new(PostgresDependencyFailure.ExecuteAsync(
            async () =>
            {
                await RevokeCoreAsync(refreshToken, deviceId, cancellationToken);
                return true;
            }).AsTask());

    private async ValueTask<TokenPair> IssueCoreAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset accessIssuedAt = NormalizeAccessTokenTimestamp(now);
        DateTimeOffset accessExpiresAt = accessIssuedAt.Add(options.Value.AccessTokenLifetime);
        DateTimeOffset refreshExpiresAt = now.Add(options.Value.RefreshTokenLifetime);
        OpaqueBearerTokenEnvelope refreshTokenEnvelope = OpaqueBearerToken.Create();
        string refreshToken = refreshTokenEnvelope.Token;
        string accessToken = accessTokenCodec.Issue(
            new AccessTokenPayload(
                userId,
                membershipId,
                organizationId,
                deviceId,
                accessIssuedAt,
                accessExpiresAt));

        await ExecuteInTransactionAsync(
            async token =>
            {
                await AcquireIssueLockAsync(userId, deviceId, token);
                await AuthenticationLifecycleCleanup.PurgeRefreshAsync(
                    context,
                    userId,
                    now,
                    token);
                string devicePrefix = BuildDevicePrefix(deviceId);
                IdentityUserToken<Guid>[] existingFamilies = await context.Set<IdentityUserToken<Guid>>()
                    .Where(candidate => candidate.UserId == userId.Value
                        && candidate.LoginProvider == RefreshLoginProvider
                        && candidate.Name.StartsWith(devicePrefix))
                    .ToArrayAsync(token);
                foreach (IdentityUserToken<Guid> existingFamily in existingFamilies)
                {
                    RefreshTokenFamilyState state = DeserializeRefreshState(existingFamily.Value);
                    if (state.RevokedAt is null)
                    {
                        existingFamily.Value = Serialize(state with
                        {
                            RevokedAt = now,
                            RevocationReason = "replaced",
                            UpdatedAt = now,
                        });
                    }
                }

                context.Set<IdentityUserToken<Guid>>().Add(
                    new IdentityUserToken<Guid>
                    {
                        UserId = userId.Value,
                        LoginProvider = RefreshLoginProvider,
                        Name = BuildRefreshName(deviceId, refreshTokenEnvelope.Locator),
                        Value = Serialize(new RefreshTokenFamilyState(
                            membershipId.Value,
                            organizationId.Value,
                            OpaqueTokenCrypto.ComputeSha256(refreshToken),
                            refreshExpiresAt,
                            now,
                            now,
                            null,
                            null,
                            0)),
                    });
            },
            cancellationToken,
            alwaysCommit: true);

        return new TokenPair(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt);
    }

    private async ValueTask<Result<TokenPair>> RotateCoreAsync(
        string refreshToken,
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        if (!OpaqueBearerToken.TryGetLocator(refreshToken, out string familyLocator))
        {
            return Result.Failure<TokenPair>(RefreshInvalid());
        }

        DateTimeOffset now = clock.UtcNow;
        Result<TokenPair> result = Result.Failure<TokenPair>(RefreshInvalid());
        bool commit = false;

        await ExecuteInTransactionAsync(
            async token =>
            {
                IdentityUserToken<Guid>? row = await LoadRefreshFamilyForUpdateAsync(
                    deviceId,
                    familyLocator,
                    token);
                if (row is null)
                {
                    result = Result.Failure<TokenPair>(RefreshInvalid());
                    return;
                }

                UserId userId = UserId.From(row.UserId);
                await AuthenticationLifecycleCleanup.PurgeRefreshAsync(
                    context,
                    userId,
                    now,
                    token);
                RefreshTokenFamilyState state = DeserializeRefreshState(row.Value);
                string candidateHash = OpaqueTokenCrypto.ComputeSha256(refreshToken);

                if (state.RevokedAt is not null)
                {
                    result = Result.Failure<TokenPair>(RefreshInvalid());
                    return;
                }

                if (Matches(state.CurrentTokenHash, candidateHash))
                {
                    if (state.CurrentExpiresAt <= now || !await MembershipActiveAsync(
                            userId,
                            state.MembershipId,
                            state.OrganizationId,
                            token))
                    {
                        state = state with
                        {
                            RevokedAt = state.RevokedAt ?? now,
                            RevocationReason = state.RevocationReason ?? "inactive",
                            UpdatedAt = now,
                        };
                        row.Value = Serialize(state);
                        result = Result.Failure<TokenPair>(RefreshInvalid());
                        commit = true;
                        return;
                    }

                    state = await PruneExpiredConsumedTokensAsync(
                        userId,
                        deviceId,
                        familyLocator,
                        state,
                        now,
                        token);

                    if (state.ConsumedTokenCount >= options.Value.RefreshFamilyConsumedTokenLimit)
                    {
                        row.Value = Serialize(state with
                        {
                            RevokedAt = now,
                            RevocationReason = "history_limit",
                            UpdatedAt = now,
                        });
                        result = Result.Failure<TokenPair>(RefreshInvalid());
                        commit = true;
                        return;
                    }

                    string newRefreshToken = OpaqueBearerToken.CreateToken(familyLocator);
                    DateTimeOffset accessIssuedAt = NormalizeAccessTokenTimestamp(now);
                    DateTimeOffset accessExpiresAt = accessIssuedAt.Add(options.Value.AccessTokenLifetime);
                    DateTimeOffset refreshExpiresAt = now.Add(options.Value.RefreshTokenLifetime);
                    string accessToken = accessTokenCodec.Issue(
                        new AccessTokenPayload(
                            userId,
                            MembershipId.From(state.MembershipId),
                            OrganizationId.From(state.OrganizationId),
                            deviceId,
                            accessIssuedAt,
                            accessExpiresAt));
                    context.Set<IdentityUserToken<Guid>>().Add(
                        new IdentityUserToken<Guid>
                        {
                            UserId = row.UserId,
                            LoginProvider = RefreshConsumedLoginProvider,
                            Name = BuildConsumedRefreshName(deviceId, familyLocator, state.CurrentTokenHash),
                            Value = Serialize(new ConsumedRefreshTokenState(state.CurrentExpiresAt, now)),
                        });
                    state = state with
                    {
                        CurrentTokenHash = OpaqueTokenCrypto.ComputeSha256(newRefreshToken),
                        CurrentExpiresAt = refreshExpiresAt,
                        ConsumedTokenCount = state.ConsumedTokenCount + 1,
                        UpdatedAt = now,
                    };
                    row.Value = Serialize(state);
                    result = Result.Success(new TokenPair(
                        accessToken,
                        accessExpiresAt,
                        newRefreshToken,
                        refreshExpiresAt));
                    commit = true;
                    return;
                }

                if (await IsConsumedRefreshTokenAsync(
                        row.UserId,
                        deviceId,
                        familyLocator,
                        candidateHash,
                        now,
                        token))
                {
                    row.Value = Serialize(state with
                    {
                        RevokedAt = now,
                        RevocationReason = "reuse",
                        UpdatedAt = now,
                    });
                    result = Result.Failure<TokenPair>(RefreshReused());
                    commit = true;
                    return;
                }

                result = Result.Failure<TokenPair>(RefreshInvalid());
            },
            cancellationToken,
            commitIfRequested: () => commit);

        return result;
    }

    private async ValueTask RevokeCoreAsync(
        string refreshToken,
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        if (!OpaqueBearerToken.TryGetLocator(refreshToken, out string familyLocator))
        {
            return;
        }

        DateTimeOffset now = clock.UtcNow;
        await ExecuteInTransactionAsync(
            async token =>
            {
                IdentityUserToken<Guid>? row = await LoadRefreshFamilyForUpdateAsync(
                    deviceId,
                    familyLocator,
                    token);
                if (row is null)
                {
                    return;
                }

                RefreshTokenFamilyState state = DeserializeRefreshState(row.Value);
                string candidateHash = OpaqueTokenCrypto.ComputeSha256(refreshToken);
                bool belongsToFamily = Matches(state.CurrentTokenHash, candidateHash)
                    || await IsConsumedRefreshTokenAsync(
                        row.UserId,
                        deviceId,
                        familyLocator,
                        candidateHash,
                        now,
                        token);
                if (!belongsToFamily || state.RevokedAt is not null)
                {
                    return;
                }

                row.Value = Serialize(state with
                {
                    RevokedAt = now,
                    RevocationReason = "logout",
                    UpdatedAt = now,
                });
            },
            cancellationToken,
            alwaysCommit: true);
    }

    private async Task<bool> MembershipActiveAsync(
        UserId userId,
        Guid membershipId,
        Guid organizationId,
        CancellationToken cancellationToken) =>
        await context.Memberships
            .AsNoTracking()
            .AnyAsync(
                candidate => candidate.Id == membershipId
                    && candidate.OrganizationId == organizationId
                    && candidate.UserId == userId.Value
                    && candidate.Status == (short)MembershipStatus.Active,
                cancellationToken);

    private async Task<IdentityUserToken<Guid>?> LoadRefreshFamilyForUpdateAsync(
        DeviceId deviceId,
        string familyLocator,
        CancellationToken cancellationToken) =>
        await context.Set<IdentityUserToken<Guid>>()
            .FromSqlInterpolated(
                $"""
                SELECT "UserId", "LoginProvider", "Name", "Value"
                FROM identity_user_tokens
                WHERE "LoginProvider" = {RefreshLoginProvider}
                  AND "Name" = {BuildRefreshName(deviceId, familyLocator)}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<bool> IsConsumedRefreshTokenAsync(
        Guid userId,
        DeviceId deviceId,
        string familyLocator,
        string candidateHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IdentityUserToken<Guid>? consumedRow = await context.Set<IdentityUserToken<Guid>>()
            .SingleOrDefaultAsync(
                candidate => candidate.UserId == userId
                    && candidate.LoginProvider == RefreshConsumedLoginProvider
                    && candidate.Name == BuildConsumedRefreshName(deviceId, familyLocator, candidateHash),
                cancellationToken);
        if (consumedRow is null)
        {
            return false;
        }

        ConsumedRefreshTokenState consumedState = DeserializeConsumedRefreshState(consumedRow.Value);
        return consumedState.ExpiresAt > now;
    }

    private async Task<RefreshTokenFamilyState> PruneExpiredConsumedTokensAsync(
        UserId userId,
        DeviceId deviceId,
        string familyLocator,
        RefreshTokenFamilyState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (state.ConsumedTokenCount < options.Value.RefreshFamilyConsumedTokenLimit)
        {
            return state;
        }

        string consumedPrefix = BuildConsumedRefreshPrefix(deviceId, familyLocator);
        IdentityUserToken<Guid>[] consumedRows = await context.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == userId.Value
                && candidate.LoginProvider == RefreshConsumedLoginProvider
                && candidate.Name.StartsWith(consumedPrefix))
            .ToArrayAsync(cancellationToken);

        List<IdentityUserToken<Guid>> expiredRows = [];
        int activeCount = 0;

        foreach (IdentityUserToken<Guid> consumedRow in consumedRows)
        {
            ConsumedRefreshTokenState consumedState = DeserializeConsumedRefreshState(consumedRow.Value);
            if (consumedState.ExpiresAt <= now)
            {
                expiredRows.Add(consumedRow);
                continue;
            }

            activeCount++;
        }

        if (expiredRows.Count > 0)
        {
            context.RemoveRange(expiredRows);
        }

        return state with
        {
            ConsumedTokenCount = activeCount,
            UpdatedAt = expiredRows.Count > 0 ? now : state.UpdatedAt,
        };
    }

    private async Task AcquireIssueLockAsync(
        UserId userId,
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        int firstKey = BuildLockKey(userId.Value.ToString("N"));
        int secondKey = BuildLockKey(deviceId.Value.ToString("N"));
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT pg_advisory_xact_lock({firstKey}, {secondKey})""",
            cancellationToken);
    }

    private async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken,
        bool alwaysCommit = false,
        Func<bool>? commitIfRequested = null)
    {
        bool ownsTransaction = context.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = ownsTransaction
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        Exception? primaryException = null;
        bool commit = alwaysCommit;

        try
        {
            await action(cancellationToken);
            commit |= commitIfRequested?.Invoke() == true;
            if (commit)
            {
                await context.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            await PostgresOwnedTransactionCleanup.CleanupAsync(
                context,
                transaction,
                rollbackOwnedTransaction: ownsTransaction && primaryException is not null,
                clearChangeTracker: primaryException is not null,
                primaryException);
        }
    }

    private static RefreshTokenFamilyState DeserializeRefreshState(string? json) =>
        JsonSerializer.Deserialize<RefreshTokenFamilyState>(json ?? string.Empty, SerializerOptions)
        ?? throw new InvalidOperationException("A refresh token family row contains invalid JSON.");

    private static ConsumedRefreshTokenState DeserializeConsumedRefreshState(string? json) =>
        JsonSerializer.Deserialize<ConsumedRefreshTokenState>(json ?? string.Empty, SerializerOptions)
        ?? throw new InvalidOperationException("A consumed refresh-token row contains invalid JSON.");

    private static string Serialize(RefreshTokenFamilyState state) =>
        JsonSerializer.Serialize(state, SerializerOptions);

    private static string Serialize(ConsumedRefreshTokenState state) =>
        JsonSerializer.Serialize(state, SerializerOptions);

    private static string BuildDevicePrefix(DeviceId deviceId) =>
        $"device:{deviceId.Value:N}:family:";

    private static string BuildRefreshName(DeviceId deviceId, string familyLocator) =>
        $"{BuildDevicePrefix(deviceId)}{familyLocator}:state";

    private static string BuildConsumedRefreshPrefix(DeviceId deviceId, string familyLocator) =>
        $"{BuildDevicePrefix(deviceId)}{familyLocator}:consumed:";

    private static string BuildConsumedRefreshName(DeviceId deviceId, string familyLocator, string tokenHash) =>
        $"{BuildConsumedRefreshPrefix(deviceId, familyLocator)}{tokenHash}";

    private static bool Matches(string storedHash, string candidateHash) =>
        OpaqueTokenCrypto.FixedTimeEquals(storedHash, candidateHash);

    private static DateTimeOffset NormalizeAccessTokenTimestamp(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    private static int BuildLockKey(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadInt32LittleEndian(hash);
    }

    private static HusayniaTabruk.Domain.Common.Errors.DomainError RefreshInvalid() =>
        AuthenticationErrorCodes.Unauthorized(
            AuthenticationErrorCodes.RefreshTokenInvalid,
            "The refresh token is invalid or has expired.");

    private static HusayniaTabruk.Domain.Common.Errors.DomainError RefreshReused() =>
        AuthenticationErrorCodes.Unauthorized(
            AuthenticationErrorCodes.RefreshTokenReused,
            "The refresh token was already used and the session has been revoked.");

    private sealed record RefreshTokenFamilyState(
        Guid MembershipId,
        Guid OrganizationId,
        string CurrentTokenHash,
        DateTimeOffset CurrentExpiresAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? RevokedAt,
        string? RevocationReason,
        int ConsumedTokenCount);

    private sealed record ConsumedRefreshTokenState(
        DateTimeOffset ExpiresAt,
        DateTimeOffset ConsumedAt);
}
