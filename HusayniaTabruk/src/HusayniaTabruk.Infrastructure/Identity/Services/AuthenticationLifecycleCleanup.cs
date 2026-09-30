using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

internal static class AuthenticationLifecycleCleanup
{
    private const string RefreshLoginProvider = "tabruk.refresh.v1";
    private const string RefreshConsumedLoginProvider = "tabruk.refresh.consumed.v1";
    private const string StepUpLoginProvider = "tabruk.stepup.v1";

    public static async Task PurgeRefreshAsync(
        TabrukDbContext context,
        UserId userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset consumedCutoff = now.Subtract(AuthenticationLifecycleRetention.ConsumedRefreshToken);
        int batchSize = AuthenticationLifecycleRetention.CleanupBatchSize;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH candidates AS (
                SELECT ctid
                FROM identity_user_tokens
                WHERE "UserId" = {userId.Value}
                  AND "LoginProvider" = {RefreshConsumedLoginProvider}
                  AND (("Value"::jsonb ->> 'expiresAt')::timestamptz) <= {consumedCutoff}
                ORDER BY
                    (("Value"::jsonb ->> 'expiresAt')::timestamptz),
                    "Name"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM identity_user_tokens AS target
            USING candidates
            WHERE target.ctid = candidates.ctid
            """,
            cancellationToken);

        DateTimeOffset familyCutoff = now.Subtract(AuthenticationLifecycleRetention.RefreshFamily);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH candidates AS (
                SELECT family.ctid
                FROM identity_user_tokens AS family
                WHERE family."UserId" = {userId.Value}
                  AND family."LoginProvider" = {RefreshLoginProvider}
                  AND GREATEST(
                        ((family."Value"::jsonb ->> 'currentExpiresAt')::timestamptz),
                        COALESCE(
                            ((family."Value"::jsonb ->> 'revokedAt')::timestamptz),
                            '-infinity'::timestamptz)) <= {familyCutoff}
                  AND NOT EXISTS (
                        SELECT 1
                        FROM identity_user_tokens AS consumed
                        WHERE consumed."UserId" = family."UserId"
                          AND consumed."LoginProvider" = {RefreshConsumedLoginProvider}
                          AND LEFT(
                                consumed."Name",
                                LENGTH(family."Name") - LENGTH(':state') + LENGTH(':consumed:'))
                              = LEFT(family."Name", LENGTH(family."Name") - LENGTH(':state'))
                                || ':consumed:')
                ORDER BY
                    GREATEST(
                        ((family."Value"::jsonb ->> 'currentExpiresAt')::timestamptz),
                        COALESCE(
                            ((family."Value"::jsonb ->> 'revokedAt')::timestamptz),
                            '-infinity'::timestamptz)),
                    family."Name"
                LIMIT {batchSize}
                FOR UPDATE OF family SKIP LOCKED
            )
            DELETE FROM identity_user_tokens AS target
            USING candidates
            WHERE target.ctid = candidates.ctid
            """,
            cancellationToken);
    }

    public static Task PurgeStepUpAsync(
        TabrukDbContext context,
        UserId userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = now.Subtract(AuthenticationLifecycleRetention.StepUpGrant);
        int batchSize = AuthenticationLifecycleRetention.CleanupBatchSize;
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH candidates AS (
                SELECT ctid
                FROM identity_user_tokens
                WHERE "UserId" = {userId.Value}
                  AND "LoginProvider" = {StepUpLoginProvider}
                  AND (
                        CASE
                            WHEN ("Value"::jsonb ->> 'consumedAt') IS NOT NULL
                                THEN (("Value"::jsonb ->> 'consumedAt')::timestamptz)
                            ELSE (("Value"::jsonb ->> 'expiresAt')::timestamptz)
                        END) <= {cutoff}
                ORDER BY
                    CASE
                        WHEN ("Value"::jsonb ->> 'consumedAt') IS NOT NULL
                            THEN (("Value"::jsonb ->> 'consumedAt')::timestamptz)
                        ELSE (("Value"::jsonb ->> 'expiresAt')::timestamptz)
                    END,
                    "Name"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM identity_user_tokens AS target
            USING candidates
            WHERE target.ctid = candidates.ctid
            """,
            cancellationToken);
    }
}
