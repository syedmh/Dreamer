using System.Text.Json;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed class IdentityStepUpVerifier(
    TabrukDbContext context,
    IIdentityService identityService,
    IClock clock,
    Microsoft.Extensions.Options.IOptions<TabrukAuthOptions> options)
    : IStepUpVerifier
{
    private const string StepUpLoginProvider = "tabruk.stepup.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<Result<StepUpGrant>> IssueAsync(
        UserId userId,
        string credential,
        StepUpPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        Result verified = await identityService.VerifyPasswordAsync(
            userId,
            credential,
            cancellationToken);
        if (verified.IsFailure)
        {
            return Result.Failure<StepUpGrant>(verified.Error);
        }

        DateTimeOffset issuedAt = clock.UtcNow;
        DateTimeOffset expiresAt = issuedAt.Add(options.Value.StepUpLifetime);
        OpaqueBearerTokenEnvelope tokenEnvelope = OpaqueBearerToken.Create();
        StepUpToken tokenValue = new(tokenEnvelope.Token);

        Result<bool> persisted = await PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async token =>
            {
                await AuthenticationLifecycleCleanup.PurgeStepUpAsync(
                    context,
                    userId,
                    issuedAt,
                    token);
                context.Set<IdentityUserToken<Guid>>().Add(
                    new IdentityUserToken<Guid>
                    {
                        UserId = userId.Value,
                        LoginProvider = StepUpLoginProvider,
                        Name = BuildStepUpName(tokenEnvelope.Locator),
                        Value = JsonSerializer.Serialize(
                            new StepUpState(
                                purpose.Value,
                                OpaqueTokenCrypto.ComputeSha256(tokenValue.Value),
                                issuedAt,
                                expiresAt,
                                null),
                            SerializerOptions),
                    });
                return Result.Success(true);
            },
            cancellationToken);

        return persisted.IsSuccess
            ? Result.Success(new StepUpGrant(tokenValue, purpose, expiresAt))
            : Result.Failure<StepUpGrant>(persisted.Error);
    }

    public ValueTask<Result> ConsumeAsync(
        UserId userId,
        StepUpToken token,
        StepUpPurpose purpose,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => ConsumeCoreAsync(userId, token, purpose, cancellationToken));

    private ValueTask<Result> ConsumeCoreAsync(
        UserId userId,
        StepUpToken token,
        StepUpPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (!OpaqueBearerToken.TryGetLocator(token.Value, out string locator))
        {
            return ValueTask.FromResult(Result.Failure(StepUpInvalid()));
        }

        return PersistenceWriteSupport.ExecuteWriteAsync(
            context,
            async ct =>
            {
                DateTimeOffset now = clock.UtcNow;
                await AuthenticationLifecycleCleanup.PurgeStepUpAsync(
                    context,
                    userId,
                    now,
                    ct);
                IdentityUserToken<Guid>? row = await context.Set<IdentityUserToken<Guid>>()
                    .FromSqlInterpolated(
                        $"""
                        SELECT "UserId", "LoginProvider", "Name", "Value"
                        FROM identity_user_tokens
                        WHERE "UserId" = {userId.Value}
                          AND "LoginProvider" = {StepUpLoginProvider}
                          AND "Name" = {BuildStepUpName(locator)}
                        FOR UPDATE
                        """)
                    .SingleOrDefaultAsync(ct);
                if (row is null)
                {
                    return Result.Failure(StepUpInvalid());
                }

                StepUpState state = DeserializeStepUpState(row.Value);
                if (!OpaqueTokenCrypto.FixedTimeEquals(state.TokenHash, OpaqueTokenCrypto.ComputeSha256(token.Value))
                    || state.ExpiresAt <= now
                    || state.ConsumedAt is not null)
                {
                    return Result.Failure(StepUpInvalid());
                }

                if (!string.Equals(state.Purpose, purpose.Value, StringComparison.Ordinal))
                {
                    return Result.Failure(
                        AuthenticationErrorCodes.Unauthorized(
                            AuthenticationErrorCodes.StepUpPurposeMismatch,
                            "The step-up token is not valid for the requested purpose."));
                }

                row.Value = JsonSerializer.Serialize(
                    state with
                    {
                        ConsumedAt = now,
                    },
                    SerializerOptions);

                return Result.Success();
            },
            cancellationToken);
    }

    private static StepUpState DeserializeStepUpState(string? json) =>
        JsonSerializer.Deserialize<StepUpState>(json ?? string.Empty, SerializerOptions)
        ?? throw new InvalidOperationException("A step-up token row contains invalid JSON.");

    private static string BuildStepUpName(string locator) => $"grant:{locator}";

    private static HusayniaTabruk.Domain.Common.Errors.DomainError StepUpInvalid() =>
        AuthenticationErrorCodes.Unauthorized(
            AuthenticationErrorCodes.StepUpInvalid,
            "The step-up token is invalid or has expired.");

    private sealed record StepUpState(
        string Purpose,
        string TokenHash,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ConsumedAt);
}
