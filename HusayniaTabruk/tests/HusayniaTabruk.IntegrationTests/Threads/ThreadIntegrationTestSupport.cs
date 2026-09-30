using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Threads;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.IntegrationTests.Persistence;
using HusayniaTabruk.IntegrationTests.Signups.Decisions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HusayniaTabruk.IntegrationTests.Threads;

internal static class ThreadIntegrationTestSupport
{
    private const long AuthorizationBoundaryAdvisoryLock = 8_191_919;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<MessageId> AppendMessageAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        MembershipId authorMembershipId,
        string body)
    {
        MessageId messageId = MessageId.New();
        await using TabrukDbContext context = database.CreateContext();
        DateThreadEntity thread = await context.DateThreads.SingleAsync(
            candidate => candidate.Id == seed.ThreadId.Value);
        context.ThreadMessages.Add(
            new ThreadMessageEntity
            {
                Id = messageId.Value,
                OrganizationId = seed.OrganizationId.Value,
                ThreadId = seed.ThreadId.Value,
                AuthorMembershipId = authorMembershipId.Value,
                ClientMessageId = IdempotencyKey.New().Value,
                Body = body,
                Visibility = (short)MessageVisibility.Visible,
                CreatedAt = seed.Now.AddMinutes(thread.Version + 1),
            });
        thread.Version++;
        await context.SaveChangesAsync();
        return messageId;
    }

    public static Task<HttpResponseMessage> GetMessagesAsync(
        HttpClient client,
        ServiceDateId serviceDateId,
        string accessToken,
        string? cursor = null,
        int? pageSize = null)
    {
        string path = $"{ApiDefaults.BasePath}/dates/{serviceDateId}/thread/messages";
        List<string> query = [];
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        if (pageSize.HasValue)
        {
            query.Add($"pageSize={pageSize.Value}");
        }

        if (query.Count > 0)
        {
            path = $"{path}?{string.Join("&", query)}";
        }

        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostMessageAsync(
        HttpClient client,
        ServiceDateId serviceDateId,
        string accessToken,
        IdempotencyKey idempotencyKey,
        long expectedVersion,
        string body)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{serviceDateId}/thread/messages")
        {
            Content = JsonContent.Create(new { body }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IdempotencyHeaderName,
            idempotencyKey.ToString());
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IfMatchHeaderName,
            $"\"{expectedVersion}\"");
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> ReportMessageAsync(
        HttpClient client,
        ServiceDateId serviceDateId,
        MessageId messageId,
        string accessToken,
        long expectedVersion,
        MessageReportReason reason,
        string? comment)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{serviceDateId}/thread/messages/{messageId}/report")
        {
            Content = JsonContent.Create(new { reason = reason.ToString(), comment }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IfMatchHeaderName,
            $"\"{expectedVersion}\"");
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> HideMessageAsync(
        HttpClient client,
        ServiceDateId serviceDateId,
        MessageId messageId,
        string accessToken,
        long expectedVersion,
        string reason)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{serviceDateId}/thread/messages/{messageId}/hide")
        {
            Content = JsonContent.Create(new { reason }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IfMatchHeaderName,
            $"\"{expectedVersion}\"");
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> LockThreadAsync(
        HttpClient client,
        ServiceDateId serviceDateId,
        string accessToken,
        long expectedVersion,
        string reason)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{serviceDateId}/thread/lock")
        {
            Content = JsonContent.Create(new { reason }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IfMatchHeaderName,
            $"\"{expectedVersion}\"");
        return client.SendAsync(request);
    }

    public static async Task<string> IssueStepUpAsync(
        HttpClient client,
        string accessToken,
        string purpose = "thread.privileged.read")
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"{ApiDefaults.BasePath}/auth/step-up")
        {
            Content = JsonContent.Create(
                new
                {
                    password = SignupDecisionTestSupport.SharedPassword,
                    purpose,
                }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        StepUpResponse result = await ReadRequiredAsync<StepUpResponse>(response, HttpStatusCode.OK);
        return result.StepUpToken;
    }

    public static Task<HttpResponseMessage> ReadPrivilegedAsync(
        HttpClient client,
        ServiceDateId serviceDateId,
        string accessToken,
        string stepUpToken,
        string reason,
        string caseId,
        string? cursor = null,
        int? pageSize = null)
    {
        Dictionary<string, object?> body = new()
        {
            ["serviceDateId"] = serviceDateId.ToString(),
            ["reason"] = reason,
            ["purpose"] = "moderation",
            ["caseId"] = caseId,
        };
        if (cursor is not null)
        {
            body["cursor"] = cursor;
        }

        if (pageSize.HasValue)
        {
            body["pageSize"] = pageSize.Value;
        }

        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/admin/moderation/thread-reads")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(AuthHeaders.StepUpToken, stepUpToken);
        return client.SendAsync(request);
    }

    public static async Task DeleteThreadAsync(
        PostgresTestDatabase database,
        ThreadId threadId)
    {
        await using TabrukDbContext context = database.CreateContext();
        DateThreadEntity thread = await context.DateThreads.SingleAsync(
            candidate => candidate.Id == threadId.Value);
        context.DateThreads.Remove(thread);
        await context.SaveChangesAsync();
    }

    public static async Task SetSignupStatusAsync(
        PostgresTestDatabase database,
        SignupId signupId,
        SignupStatus status,
        DateTimeOffset transitionedAt)
    {
        await using TabrukDbContext context = database.CreateContext();
        SignupEntity signup = await context.Signups.SingleAsync(
            candidate => candidate.Id == signupId.Value);
        signup.Status = checked((short)status);
        signup.LastTransitionAt = status == SignupStatus.Pending ? null : transitionedAt;
        signup.WaitlistOrder = status == SignupStatus.Waitlisted ? 1 : null;
        signup.Version++;
        await context.SaveChangesAsync();
    }

    public static async Task CreatePrivilegedAuditFailureTriggerAsync(
        PostgresTestDatabase database)
    {
        string functionName = $"t19_fail_privileged_audit_{Guid.NewGuid():N}";
        await using NpgsqlConnection connection = new(database.AdministrativeConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            $"""
            SET search_path TO "{database.Schema}";
            CREATE FUNCTION {functionName}() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'injected t19 privileged audit failure'
                    USING ERRCODE = '08006';
            END
            $$;
            CREATE TRIGGER {functionName}_trigger
            BEFORE INSERT ON privileged_access_events
            FOR EACH ROW EXECUTE FUNCTION {functionName}();
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<NpgsqlConnection> HoldThreadMutationBoundaryAsync(
        PostgresTestDatabase database)
    {
        await using NpgsqlConnection setup = new(database.AdministrativeConnectionString);
        await setup.OpenAsync();
        await using NpgsqlCommand trigger = new(
            $"""
            SET search_path TO "{database.Schema}";
            CREATE FUNCTION t19_hold_thread_mutation() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                PERFORM pg_advisory_lock({AuthorizationBoundaryAdvisoryLock});
                PERFORM pg_advisory_unlock({AuthorizationBoundaryAdvisoryLock});
                RETURN NEW;
            END
            $$;
            CREATE TRIGGER t19_hold_thread_mutation_trigger
            BEFORE INSERT ON thread_messages
            FOR EACH ROW EXECUTE FUNCTION t19_hold_thread_mutation();
            """,
            setup);
        await trigger.ExecuteNonQueryAsync();

        NpgsqlConnection blocker = new(database.AdministrativeConnectionString);
        await blocker.OpenAsync();
        await using NpgsqlCommand hold = new(
            $"SELECT pg_advisory_lock({AuthorizationBoundaryAdvisoryLock})",
            blocker);
        await hold.ExecuteNonQueryAsync();
        return blocker;
    }

    public static async Task ReleaseThreadMutationBoundaryAsync(NpgsqlConnection blocker)
    {
        await using NpgsqlCommand release = new(
            $"SELECT pg_advisory_unlock({AuthorizationBoundaryAdvisoryLock})",
            blocker);
        await release.ExecuteNonQueryAsync();
        await blocker.DisposeAsync();
    }

    public static async Task WaitForThreadMutationBoundaryAsync(
        PostgresTestDatabase database,
        string applicationName,
        Task requestTask)
    {
        await using NpgsqlConnection observer = new(database.AdministrativeConnectionString);
        await observer.OpenAsync();
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Assert.False(
                requestTask.IsCompleted,
                "The thread request completed before reaching the mutation boundary.");
            await using NpgsqlCommand query = new(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE application_name = @applicationName
                      AND wait_event = 'advisory')
                """,
                observer);
            query.Parameters.AddWithValue("applicationName", applicationName);
            if ((bool)(await query.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("Missing activity result.")))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for the thread mutation boundary.");
    }

    public static async Task ReassignManagerWithShortLockTimeoutAsync(
        PostgresTestDatabase database,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        MembershipId managerMembershipId)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SET lock_timeout = '250ms';
            UPDATE service_dates
            SET manager_membership_id = @managerMembershipId
            WHERE organization_id = @organizationId
              AND id = @serviceDateId
            """,
            connection);
        command.Parameters.AddWithValue("organizationId", organizationId.Value);
        command.Parameters.AddWithValue("serviceDateId", serviceDateId.Value);
        command.Parameters.AddWithValue("managerMembershipId", managerMembershipId.Value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task WithdrawSignupWithShortLockTimeoutAsync(
        PostgresTestDatabase database,
        SignupId signupId,
        DateTimeOffset transitionedAt)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SET lock_timeout = '250ms';
            UPDATE signups
            SET status = @status,
                last_transition_at = @transitionedAt,
                version = version + 1
            WHERE id = @signupId
            """,
            connection);
        command.Parameters.AddWithValue("status", (short)SignupStatus.Withdrawn);
        command.Parameters.AddWithValue("transitionedAt", transitionedAt);
        command.Parameters.AddWithValue("signupId", signupId.Value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<PendingOrdinaryRevocation> BeginOrdinaryRevocationAsync(
        PostgresTestDatabase database,
        string scenario,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        MembershipId actorMembershipId,
        MembershipId replacementManagerMembershipId,
        SignupId signupId,
        DateTimeOffset changedAt)
    {
        NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        try
        {
            string sql = scenario switch
            {
                "withdrawal" =>
                    """
                    UPDATE signups
                    SET status = @withdrawn,
                        last_transition_at = @changedAt,
                        version = version + 1
                    WHERE id = @signupId
                    """,
                "manager-reassignment" =>
                    """
                    UPDATE service_dates
                    SET manager_membership_id = @replacementManagerMembershipId,
                        version = version + 1
                    WHERE organization_id = @organizationId
                      AND id = @serviceDateId
                    """,
                "role-revocation" =>
                    """
                    UPDATE role_assignments
                    SET revoked_at = @changedAt,
                        revoked_by_membership_id = @actorMembershipId
                    WHERE organization_id = @organizationId
                      AND membership_id = @actorMembershipId
                      AND role = @foodInchargeRole
                      AND revoked_at IS NULL
                    """,
                "membership-disable" =>
                    """
                    UPDATE memberships
                    SET status = @disabled
                    WHERE organization_id = @organizationId
                      AND id = @actorMembershipId
                    """,
                "date-cancellation" =>
                    """
                    UPDATE service_dates
                    SET status = @cancelled,
                        version = version + 1
                    WHERE organization_id = @organizationId
                      AND id = @serviceDateId
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("withdrawn", (short)SignupStatus.Withdrawn);
            command.Parameters.AddWithValue("disabled", (short)MembershipStatus.Disabled);
            command.Parameters.AddWithValue("cancelled", (short)ServiceDateStatus.Cancelled);
            command.Parameters.AddWithValue("foodInchargeRole", (short)OrganizationRole.FoodIncharge);
            command.Parameters.AddWithValue("changedAt", changedAt);
            command.Parameters.AddWithValue("organizationId", organizationId.Value);
            command.Parameters.AddWithValue("serviceDateId", serviceDateId.Value);
            command.Parameters.AddWithValue("actorMembershipId", actorMembershipId.Value);
            command.Parameters.AddWithValue(
                "replacementManagerMembershipId",
                replacementManagerMembershipId.Value);
            command.Parameters.AddWithValue("signupId", signupId.Value);
            int affected = await command.ExecuteNonQueryAsync();
            Assert.Equal(1, affected);
            return new PendingOrdinaryRevocation(connection, transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public static async Task RevokeRoleAsync(
        PostgresTestDatabase database,
        OrganizationId organizationId,
        MembershipId membershipId,
        OrganizationRole role,
        DateTimeOffset revokedAt,
        bool shortLockTimeout)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            $"""
            {(shortLockTimeout ? "SET lock_timeout = '250ms';" : string.Empty)}
            UPDATE role_assignments
            SET revoked_at = @revokedAt
            WHERE organization_id = @organizationId
              AND membership_id = @membershipId
              AND role = @role
              AND revoked_at IS NULL
            """,
            connection);
        command.Parameters.AddWithValue("revokedAt", revokedAt);
        command.Parameters.AddWithValue("organizationId", organizationId.Value);
        command.Parameters.AddWithValue("membershipId", membershipId.Value);
        command.Parameters.AddWithValue("role", (short)role);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task LockRoleForUpdateNowaitAsync(
        PostgresTestDatabase database,
        OrganizationId organizationId,
        MembershipId membershipId,
        OrganizationRole role)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT *
            FROM role_assignments
            WHERE organization_id = @organizationId
              AND membership_id = @membershipId
              AND role = @role
              AND revoked_at IS NULL
            FOR UPDATE NOWAIT
            """,
            connection);
        command.Parameters.AddWithValue("organizationId", organizationId.Value);
        command.Parameters.AddWithValue("membershipId", membershipId.Value);
        command.Parameters.AddWithValue("role", (short)role);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task DropPrivilegedAuditFailureTriggersAsync(
        PostgresTestDatabase database)
    {
        await using NpgsqlConnection connection = new(database.AdministrativeConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            $"""
            SET search_path TO "{database.Schema}";
            DO $body$
            DECLARE
                trigger_row record;
            BEGIN
                FOR trigger_row IN
                    SELECT trigger_name
                    FROM information_schema.triggers
                    WHERE event_object_schema = '{database.Schema}'
                      AND event_object_table = 'privileged_access_events'
                      AND trigger_name LIKE 't19_fail_privileged_audit_%'
                LOOP
                    EXECUTE format(
                        'DROP TRIGGER %I ON privileged_access_events',
                        trigger_row.trigger_name);
                END LOOP;
            END
            $body$;
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    public static string HashResponse(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    public static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.True(
            response.StatusCode == expectedStatus,
            $"Expected {expectedStatus}, received {response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions)
            ?? throw new InvalidOperationException($"Missing {typeof(T).Name} response body.");
    }

    internal sealed record StepUpResponse(
        string StepUpToken,
        string Purpose,
        DateTimeOffset ExpiresAt);

    internal sealed record ThreadMessageResponse(
        string Id,
        string SenderDisplayName,
        string Body,
        string Visibility,
        DateTimeOffset CreatedAt);

    internal sealed record ThreadMessagePageResponse(
        string ServiceDateId,
        string Status,
        DateTimeOffset? LockedAt,
        IReadOnlyList<ThreadMessageResponse> Items,
        string? NextCursor);

    internal sealed record PrivilegedThreadMessageResponse(
        string Id,
        string SenderDisplayName,
        string Body,
        string Visibility,
        DateTimeOffset CreatedAt,
        DateTimeOffset? HiddenAt);

    internal sealed record PrivilegedThreadMessagePageResponse(
        string ThreadId,
        string ServiceDateId,
        string Status,
        DateTimeOffset? LockedAt,
        IReadOnlyList<PrivilegedThreadMessageResponse> Items,
        string? NextCursor);

    internal sealed class PendingOrdinaryRevocation(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async Task CommitAsync() => await transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
