using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Domain.Forms;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Husaynia.Infrastructure.Persistence.Core;
using Husaynia.IntegrationTests.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Husaynia.IntegrationTests.Forms;

public sealed class FormsWebBoundaryTests
{
    private const int AdminOperationCount = 8;
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AntiforgeryHoneypotMalformedAndOversizedRequestsCreateNoPartialState()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AntiforgeryHoneypotMalformedAndOversizedRequestsCreateNoPartialState));
        using var factory = CreateFactory(database);
        await SeedDefinitionAsync(factory);
        using var client = factory.CreateIdentityClient();

        using var missingToken = await client.PostAsJsonAsync(
            "/forms/submissions",
            ValidRequest());
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        await AssertNoWorkflowAsync(factory);

        var token = await GetFormsAntiforgeryTokenAsync(client);
        using var bot = await PostWithTokenAsync(
            client,
            "/forms/submissions",
            ValidRequest() with { Honeypot = "filled" },
            token);
        Assert.Equal(HttpStatusCode.Accepted, bot.StatusCode);
        await AssertNoWorkflowAsync(factory);

        token = await GetFormsAntiforgeryTokenAsync(client);
        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/forms/submissions")
        {
            Content = new StringContent(
                """{"formKey":"test-contact-v1","fields":{"email":"person@example.test"},"consentVersion":"test-consent-v1","honeypot":"","unexpected":true}""",
                Encoding.UTF8,
                "application/json"),
        };
        malformed.Headers.Add("RequestVerificationToken", token);
        using var malformedResponse = await client.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        await AssertNoWorkflowAsync(factory);

        token = await GetFormsAntiforgeryTokenAsync(client);
        using var unknown = await PostWithTokenAsync(
            client,
            "/forms/submissions",
            ValidRequest() with { FormKey = "test-unknown-v1" },
            token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain(
            "email",
            await unknown.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        await AssertNoWorkflowAsync(factory);

        token = await GetFormsAntiforgeryTokenAsync(client);
        using var oversized = new HttpRequestMessage(HttpMethod.Post, "/forms/submissions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(ValidRequest() with
                {
                    Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["email"] = new('x', 70_000),
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        };
        oversized.Headers.Add("RequestVerificationToken", token);
        using var oversizedResponse = await client.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);
        await AssertNoWorkflowAsync(factory);
    }

    [Fact]
    public async Task ValidAndDuplicatePostsReturnOneReceiptAndOneDurableWorkflow()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ValidAndDuplicatePostsReturnOneReceiptAndOneDurableWorkflow));
        using var factory = CreateFactory(database);
        await SeedDefinitionAsync(factory);
        using var client = factory.CreateIdentityClient();
        var token = await GetFormsAntiforgeryTokenAsync(client);

        using var first = await PostWithTokenAsync(
            client,
            "/forms/submissions",
            ValidRequest(),
            token);
        token = await GetFormsAntiforgeryTokenAsync(client);
        using var second = await PostWithTokenAsync(
            client,
            "/forms/submissions",
            ValidRequest(),
            token);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<FormSubmissionResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<FormSubmissionResponse>();
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        Assert.Equal(firstBody!.SubmissionId, secondBody!.SubmissionId);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        Assert.Equal(1, await context.Set<FormSubmission>().CountAsync());
        Assert.Equal(1, await context.Set<FormSubmissionValue>().CountAsync());
        Assert.Equal(1, await context.Set<Husaynia.Domain.Operations.Jobs.JobInstance>().CountAsync());
        Assert.Single(
            await context.Set<AuditEvent>()
                .Where(audit => audit.Action == "forms.submission.accept")
                .ToArrayAsync());
    }

    [Fact]
    public async Task AdminRequiresSiteAdministratorMfaAndWritesExactlyOneRedactedAudit()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AdminRequiresSiteAdministratorMfaAndWritesExactlyOneRedactedAudit));
        using var factory = CreateFactory(database);
        await SeedDefinitionAsync(factory);
        var password = "FormsIntegration!234";
        var auditor = await factory.SeedUserAsync(
            "forms-auditor@example.test",
            password,
            [RoleNames.ReadOnlyAuditor],
            enableMfa: true);
        using var auditorClient = await factory.CreateAuthenticatedClientAsync(
            auditor,
            [RoleNames.ReadOnlyAuditor],
            mfaSatisfied: true);
        auditorClient.DefaultRequestHeaders.Add("X-Correlation-ID", "forms-auditor-denied");

        using var denied = await auditorClient.GetAsync("/admin/forms/submissions?take=10");

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedAudits = (await factory.ReadAuditEventsAsync())
            .Where(audit =>
                audit.Action == "forms.submission.read" &&
                audit.CorrelationId == "forms-auditor-denied")
            .ToArray();
        var deniedAudit = Assert.Single(deniedAudits);
        Assert.Equal("denied", deniedAudit.Outcome);
        Assert.DoesNotContain("example.test", deniedAudit.DetailJson, StringComparison.Ordinal);

        var administrator = await factory.SeedUserAsync(
            "forms-admin@example.test",
            password,
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var administratorClient = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: true);
        administratorClient.DefaultRequestHeaders.Add(
            "X-Correlation-ID",
            "forms-admin-read");

        using var allowed = await administratorClient.GetAsync(
            "/admin/forms/submissions?take=10");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var allowedAudits = (await factory.ReadAuditEventsAsync())
            .Where(audit =>
                audit.Action == "forms.submission.read" &&
                audit.CorrelationId == "forms-admin-read")
            .ToArray();
        var allowedAudit = Assert.Single(allowedAudits);
        Assert.Equal("allowed", allowedAudit.Outcome);
        Assert.DoesNotContain("example.test", allowedAudit.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SiteAdministratorWithoutMfaIsDeniedAndAuditedOnce()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(SiteAdministratorWithoutMfaIsDeniedAndAuditedOnce));
        using var factory = CreateFactory(database);
        var administrator = await factory.SeedUserAsync(
            "forms-no-mfa@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: false);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: false);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "forms-admin-no-mfa");

        using var response = await client.GetAsync("/admin/forms/submissions?take=10");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var audits = (await factory.ReadAuditEventsAsync())
            .Where(audit =>
                audit.Action == "forms.submission.read" &&
                audit.CorrelationId == "forms-admin-no-mfa")
            .ToArray();
        var audit = Assert.Single(audits);
        Assert.Equal("denied", audit.Outcome);
        Assert.Contains("mfa_required", audit.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminMutationAntiforgeryFailureIsAuditedOnceWithoutMutation()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AdminMutationAntiforgeryFailureIsAuditedOnceWithoutMutation));
        using var factory = CreateFactory(database);
        var administrator = await factory.SeedUserAsync(
            "forms-csrf-admin@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: true);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "forms-admin-csrf");

        using var response = await client.PostAsJsonAsync(
            "/admin/forms/definitions/test-contact-v1/publish",
            new
            {
                title = "Synthetic",
                destinationKey = "test-destination",
                templateKey = "test-template",
                consentVersion = "test-consent-v1",
                fields = Array.Empty<object>(),
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        Assert.Equal(0, await context.Set<FormDefinition>().CountAsync());
        var audits = (await factory.ReadAuditEventsAsync())
            .Where(audit =>
                audit.Action == "forms.definition.publish" &&
                audit.CorrelationId == "forms-admin-csrf")
            .ToArray();
        var audit = Assert.Single(audits);
        Assert.Equal("allowed", audit.Outcome);
        Assert.Contains("antiforgery_failed", audit.DetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", audit.DetailJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StaleSessionKind.Disabled)]
    [InlineData(StaleSessionKind.RevokedAdministrator)]
    [InlineData(StaleSessionKind.ChangedSecurityStamp)]
    [InlineData(StaleSessionKind.DeletedUser)]
    [InlineData(StaleSessionKind.MissingSecurityStamp)]
    [InlineData(StaleSessionKind.RevokedMfa)]
    public async Task StaleSessionsAreDeniedBeforeEveryAdminOperationAndAuditedOnce(
        StaleSessionKind staleSessionKind)
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            $"{nameof(StaleSessionsAreDeniedBeforeEveryAdminOperationAndAuditedOnce)}_{staleSessionKind}");
        using var factory = CreateFactory(database);
        await SeedDefinitionAsync(factory);
        var administrator = await factory.SeedUserAsync(
            $"forms-stale-{staleSessionKind}@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        var clients = staleSessionKind == StaleSessionKind.MissingSecurityStamp
            ? await CreateClientsWithoutSecurityStampAsync(
                factory,
                administrator,
                AdminOperationCount)
            : await CreateAuthenticatedClientsAsync(
                factory,
                administrator,
                AdminOperationCount);
        try
        {
            await InvalidateSessionAsync(factory, administrator.UserId, staleSessionKind);
            var operations = CreateAdminOperations(Guid.NewGuid(), $"stale-{staleSessionKind}");

            for (var index = 0; index < operations.Count; index++)
            {
                using var response = await SendAdminOperationAsync(
                    clients[index],
                    operations[index],
                    antiforgeryToken: null);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            var audits = await factory.ReadAuditEventsAsync();
            foreach (var operation in operations)
            {
                var audit = Assert.Single(
                    audits,
                    candidate => candidate.CorrelationId == operation.CorrelationId);
                Assert.Equal(operation.Action, audit.Action);
                Assert.Equal("denied", audit.Outcome);
                Assert.Null(audit.ActorId);
                Assert.Equal("[]", audit.RolesJson);
                Assert.Contains("unauthenticated", audit.DetailJson, StringComparison.Ordinal);
                Assert.DoesNotContain("example.test", audit.DetailJson, StringComparison.Ordinal);
            }

            using var scope = factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
            Assert.Equal(1, await context.Set<FormDefinition>().CountAsync());
            Assert.Equal(1, await context.Set<FormDefinitionVersion>().CountAsync());
            Assert.Equal(0, await context.Set<FormSubmission>().CountAsync());
            Assert.Equal(
                0,
                await context.Set<Husaynia.Domain.Operations.Jobs.JobInstance>().CountAsync());
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task CurrentSessionCanExecuteEveryAdminOperationWithOneAuditEach()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(CurrentSessionCanExecuteEveryAdminOperationWithOneAuditEach));
        using var factory = CreateFactory(database);
        await SeedDefinitionAsync(factory);
        var submissionId = await SeedDeadLetteredSubmissionAsync(factory);
        var administrator = await factory.SeedUserAsync(
            "forms-current-admin@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: true);
        var operations = new List<AdminOperation>();

        var definitionRead = new AdminOperation(
            HttpMethod.Get,
            "/admin/forms/definitions/test-contact-v1",
            null,
            FormAdministrationService.DefinitionReadAction,
            "current-definition-read");
        operations.Add(definitionRead);
        using var definitionReadResponse = await SendAdminOperationAsync(
            client,
            definitionRead,
            antiforgeryToken: null);
        Assert.Equal(HttpStatusCode.OK, definitionReadResponse.StatusCode);
        var definitionRowVersion = ReadEntityTag(definitionReadResponse);

        var submissionRead = new AdminOperation(
            HttpMethod.Get,
            "/admin/forms/submissions?take=10",
            null,
            FormAdministrationService.SubmissionReadAction,
            "current-submission-read");
        operations.Add(submissionRead);
        using var submissionReadResponse = await SendAdminOperationAsync(
            client,
            submissionRead,
            antiforgeryToken: null);
        Assert.Equal(HttpStatusCode.OK, submissionReadResponse.StatusCode);

        var publish = CreatePublishOperation("current", definitionRowVersion);
        operations.Add(publish);
        using var publishResponse = await SendAdminOperationAsync(
            client,
            publish,
            await GetFormsAntiforgeryTokenAsync(client));
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        Assert.Equal(8, ReadEntityTag(publishResponse).Length);

        var retry = CreateRetryOperation(submissionId, "current");
        operations.Add(retry);
        using var retryResponse = await SendAdminOperationAsync(
            client,
            retry,
            await GetFormsAntiforgeryTokenAsync(client));
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);

        var submissionRowVersion = await ReadSubmissionRowVersionAsync(
            factory,
            submissionId);
        foreach (var action in new[]
                 {
                     FormRetentionAction.PlaceLegalHold,
                     FormRetentionAction.ReleaseLegalHold,
                     FormRetentionAction.MarkEligible,
                     FormRetentionAction.Anonymize,
                 })
        {
            var retention = CreateRetentionOperation(
                submissionId,
                "current",
                action,
                submissionRowVersion);
            operations.Add(retention);
            using var response = await SendAdminOperationAsync(
                client,
                retention,
                await GetFormsAntiforgeryTokenAsync(client));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            submissionRowVersion = ReadEntityTag(response);
        }

        var audits = await factory.ReadAuditEventsAsync();
        foreach (var operation in operations)
        {
            var audit = Assert.Single(
                audits,
                candidate => candidate.CorrelationId == operation.CorrelationId);
            Assert.Equal(operation.Action, audit.Action);
            Assert.Equal("allowed", audit.Outcome);
            Assert.Equal(administrator.UserId, audit.ActorId);
            Assert.DoesNotContain("example.test", audit.DetailJson, StringComparison.Ordinal);
        }

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        var submission = await context.Set<FormSubmission>()
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == submissionId);
        Assert.Equal(FormRetentionStatus.Anonymized, submission.RetentionStatus);
        Assert.False(submission.HasLegalHold);
        Assert.Equal(
            string.Empty,
            await context.Set<FormSubmissionValue>()
                .Where(value => value.SubmissionId == submissionId)
                .Select(value => value.Value)
                .SingleAsync());
    }

    [Fact]
    public async Task StaleSessionAuditFailureReturnsServiceUnavailableWithoutStoreAccess()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(StaleSessionAuditFailureReturnsServiceUnavailableWithoutStoreAccess));
        var finalizer = new ThrowingAuditFinalizer();
        using var factory = CreateFactory(
            database,
            services =>
            {
                services.RemoveAll<IIdentityAuditFinalizer>();
                services.AddSingleton<IIdentityAuditFinalizer>(finalizer);
            });
        await SeedDefinitionAsync(factory);
        var administrator = await factory.SeedUserAsync(
            "forms-audit-failure@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        var clients = await CreateClientsWithoutSecurityStampAsync(
            factory,
            administrator,
            count: 1);
        using var client = clients[0];

        using var response = await client.GetAsync(
            "/admin/forms/definitions/test-contact-v1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, finalizer.CallCount);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        Assert.Equal(1, await context.Set<FormDefinition>().CountAsync());
        Assert.Equal(0, await context.Set<FormSubmission>().CountAsync());
    }

    [Fact]
    public async Task UnknownFormKeysDoNotCreateRateLimitPartitions()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(UnknownFormKeysDoNotCreateRateLimitPartitions));
        using var factory = CreateFactory(database);
        using var client = factory.CreateIdentityClient();
        var token = await GetFormsAntiforgeryTokenAsync(client);

        for (var index = 0; index < 25; index++)
        {
            using var response = await PostWithTokenAsync(
                client,
                "/forms/submissions",
                ValidRequest() with { FormKey = $"test-unknown-{index}" },
                token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        Assert.Equal(0, await context.Set<FormRateLimit>().CountAsync());
        Assert.Equal(0, await context.Set<FormSubmission>().CountAsync());
    }

    [Fact]
    public async Task ProductionHostRejectsPickupEvenWhenConfigurationClaimsDevelopment()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ProductionHostRejectsPickupEvenWhenConfigurationClaimsDevelopment));
        using var factory = new IdentityWebApplicationFactory(
            database,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Forms:Enabled"] = "false",
                ["Forms:EnvironmentName"] = "Development",
                ["Forms:Delivery:Mode"] = "Pickup",
                ["Forms:Delivery:PickupDirectory"] = Path.GetFullPath("forms-production-pickup"),
                ["Forms:Delivery:PickupDestinationKey"] = "test-destination",
            },
            environmentName: "Production");

        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);

        Assert.Contains(
            "pickup delivery is prohibited",
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedNestedDefinitionDtosReturnAuditedBadRequest()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(MalformedNestedDefinitionDtosReturnAuditedBadRequest));
        using var factory = CreateFactory(database);
        await SeedDefinitionAsync(factory);
        var administrator = await factory.SeedUserAsync(
            "forms-malformed-admin@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: true);
        var malformedBodies = new[]
        {
            """
            {"title":"Synthetic","destinationKey":"test-destination","templateKey":"test-template","consentVersion":"test","fields":[null],"expectedStateRowVersion":"AAAAAAAAAAA="}
            """,
            """
            {"title":"Synthetic","destinationKey":"test-destination","templateKey":"test-template","consentVersion":"test","fields":[{}],"expectedStateRowVersion":"AAAAAAAAAAA="}
            """,
            """
            {"title":"Synthetic","destinationKey":"test-destination","templateKey":"test-template","consentVersion":"test","fields":[{"key":"email","label":"Email","kind":"Email","required":true,"minimumLength":3,"maximumLength":320,"minimumValue":null,"maximumValue":null,"patternKind":"Email","choices":null,"order":1,"privacyClass":"Contact"}],"expectedStateRowVersion":"AAAAAAAAAAA="}
            """,
            """
            {"title":"Synthetic","destinationKey":"test-destination","templateKey":"test-template","consentVersion":"test","fields":[{"key":"email","label":"Email","kind":"Unknown","required":true,"minimumLength":3,"maximumLength":320,"minimumValue":null,"maximumValue":null,"patternKind":"Email","choices":[],"order":1,"privacyClass":"Contact"}],"expectedStateRowVersion":"AAAAAAAAAAA="}
            """,
        };

        for (var index = 0; index < malformedBodies.Length; index++)
        {
            var correlationId = $"forms-malformed-{index}";
            using var response = await SendRawWithTokenAsync(
                client,
                "/admin/forms/definitions/test-contact-v1/publish",
                malformedBodies[index],
                await GetFormsAntiforgeryTokenAsync(client),
                correlationId);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var audit = Assert.Single(
                await factory.ReadAuditEventsAsync(),
                candidate => candidate.CorrelationId == correlationId);
            Assert.Equal(FormAdministrationService.DefinitionPublishAction, audit.Action);
            Assert.Equal("allowed", audit.Outcome);
            Assert.Contains("invalid_transport", audit.DetailJson, StringComparison.Ordinal);
        }

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        Assert.Equal(1, await context.Set<FormDefinitionVersion>().CountAsync());
    }

    [Fact]
    public async Task ThrowingAdministrationIsExceptionAuditedAcrossEveryEndpoint()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ThrowingAdministrationIsExceptionAuditedAcrossEveryEndpoint));
        using var factory = CreateFactory(
            database,
            services =>
            {
                services.RemoveAll<IFormAdministration>();
                services.AddSingleton<IFormAdministration>(
                    new ThrowingFormAdministration());
            });
        await SeedDefinitionAsync(factory);
        var administrator = await factory.SeedUserAsync(
            "forms-throwing-service@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: true);
        var operations = CreateAdminOperations(Guid.NewGuid(), "throwing-service");

        foreach (var operation in operations)
        {
            var token = operation.Body is null
                ? null
                : await GetFormsAntiforgeryTokenAsync(client);
            using var response = await SendAdminOperationAsync(client, operation, token);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        var audits = await factory.ReadAuditEventsAsync();
        foreach (var operation in operations)
        {
            var audit = Assert.Single(
                audits,
                candidate => candidate.CorrelationId == operation.CorrelationId);
            Assert.Equal("allowed", audit.Outcome);
            Assert.Contains("unexpected_failure", audit.DetailJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ThrowingStoreIsExceptionAuditedOnce()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ThrowingStoreIsExceptionAuditedOnce));
        using var factory = CreateFactory(
            database,
            services =>
            {
                services.RemoveAll<IFormAdministrationStore>();
                services.AddScoped<IFormAdministrationStore, ThrowingFormAdministrationStore>();
            });
        await SeedDefinitionAsync(factory);
        var administrator = await factory.SeedUserAsync(
            "forms-throwing-store@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: true);
        client.DefaultRequestHeaders.Add(
            "X-Correlation-ID",
            "forms-throwing-store");

        using var response = await client.GetAsync(
            "/admin/forms/definitions/test-contact-v1");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var audit = Assert.Single(
            await factory.ReadAuditEventsAsync(),
            candidate => candidate.CorrelationId == "forms-throwing-store");
        Assert.Equal("allowed", audit.Outcome);
        Assert.Contains("unexpected_failure", audit.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleHttpConcurrencyTokensReturnConflictWithoutLastWriteWins()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(StaleHttpConcurrencyTokensReturnConflictWithoutLastWriteWins));
        using var factory = CreateFactory(database);
        await SeedDefinitionAsync(factory);
        var submissionId = await SeedDeadLetteredSubmissionAsync(factory);
        var administrator = await factory.SeedUserAsync(
            "forms-cas-admin@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: true);

        var read = new AdminOperation(
            HttpMethod.Get,
            "/admin/forms/definitions/test-contact-v1",
            null,
            FormAdministrationService.DefinitionReadAction,
            "forms-cas-read");
        using var readResponse = await SendAdminOperationAsync(
            client,
            read,
            antiforgeryToken: null);
        var definitionRowVersion = ReadEntityTag(readResponse);
        var publish = CreatePublishOperation("forms-cas-first", definitionRowVersion);
        using var published = await SendAdminOperationAsync(
            client,
            publish,
            await GetFormsAntiforgeryTokenAsync(client));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var stalePublish = CreatePublishOperation("forms-cas-stale", definitionRowVersion);
        using var publishConflict = await SendAdminOperationAsync(
            client,
            stalePublish,
            await GetFormsAntiforgeryTokenAsync(client));
        Assert.Equal(HttpStatusCode.Conflict, publishConflict.StatusCode);

        var submissionRowVersion = await ReadSubmissionRowVersionAsync(
            factory,
            submissionId);
        var eligible = CreateRetentionOperation(
            submissionId,
            "forms-cas-first",
            FormRetentionAction.MarkEligible,
            submissionRowVersion);
        using var eligibleResponse = await SendAdminOperationAsync(
            client,
            eligible,
            await GetFormsAntiforgeryTokenAsync(client));
        Assert.Equal(HttpStatusCode.OK, eligibleResponse.StatusCode);
        var staleHold = CreateRetentionOperation(
            submissionId,
            "forms-cas-stale",
            FormRetentionAction.PlaceLegalHold,
            submissionRowVersion);
        using var retentionConflict = await SendAdminOperationAsync(
            client,
            staleHold,
            await GetFormsAntiforgeryTokenAsync(client));
        Assert.Equal(HttpStatusCode.Conflict, retentionConflict.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        Assert.Equal(2, await context.Set<FormDefinitionVersion>().CountAsync());
        Assert.False(
            await context.Set<FormSubmission>()
                .Where(submission => submission.Id == submissionId)
                .Select(submission => submission.HasLegalHold)
                .SingleAsync());
    }

    [Theory]
    [InlineData(BoundaryFailure.Authentication)]
    [InlineData(BoundaryFailure.UserLookup)]
    [InlineData(BoundaryFailure.Policy)]
    [InlineData(BoundaryFailure.Antiforgery)]
    [InlineData(BoundaryFailure.Body)]
    public async Task OutermostBoundaryAuditsDependencyFailuresExactlyOnce(
        BoundaryFailure failure)
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            $"{nameof(OutermostBoundaryAuditsDependencyFailuresExactlyOnce)}_{failure}");
        var userLookupFailure = new FailureToggle();
        using var factory = CreateFactory(
            database,
            services => ConfigureBoundaryFailure(
                services,
                failure,
                userLookupFailure));
        await SeedDefinitionAsync(factory);
        var administrator = await factory.SeedUserAsync(
            $"forms-boundary-{failure}@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: true);
        userLookupFailure.Enabled = true;
        var correlationId = $"forms-boundary-{failure}";

        using var response = failure switch
        {
            BoundaryFailure.Antiforgery => await SendRawWithTokenAsync(
                client,
                "/admin/forms/definitions/test-contact-v1/publish",
                CreateValidPublishJson(),
                "synthetic-token",
                correlationId),
            BoundaryFailure.Body => await SendThrowingBodyRequestAsync(
                client,
                correlationId,
                await GetFormsAntiforgeryTokenAsync(client)),
            _ => await SendGetWithCorrelationAsync(
                client,
                "/admin/forms/definitions/test-contact-v1",
                correlationId),
        };

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var audit = Assert.Single(
            await factory.ReadAuditEventsAsync(),
            candidate => candidate.CorrelationId == correlationId);
        Assert.Equal(
            failure is BoundaryFailure.Antiforgery or BoundaryFailure.Body
                ? FormAdministrationService.DefinitionPublishAction
                : FormAdministrationService.DefinitionReadAction,
            audit.Action);
        Assert.Equal(
            failure is BoundaryFailure.Authentication or BoundaryFailure.UserLookup
                ? "denied"
                : "allowed",
            audit.Outcome);
        Assert.Contains("unexpected_failure", audit.DetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", audit.DetailJson, StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        Assert.Equal(1, await context.Set<FormDefinitionVersion>().CountAsync());
    }

    [Fact]
    public async Task ClientCancellationIsAuditedOnceWithoutUnauditedRethrow()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ClientCancellationIsAuditedOnceWithoutUnauditedRethrow));
        var administration = new CancellingFormAdministration();
        using var factory = CreateFactory(
            database,
            services =>
            {
                services.RemoveAll<IFormAdministration>();
                services.AddSingleton<IFormAdministration>(administration);
            });
        await SeedDefinitionAsync(factory);
        var administrator = await factory.SeedUserAsync(
            "forms-cancel-admin@example.test",
            "FormsIntegration!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            mfaSatisfied: true);
        const string correlationId = "forms-client-cancel";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/forms/definitions/test-contact-v1");
        request.Headers.Add("X-Correlation-ID", correlationId);
        using var cancellation = new CancellationTokenSource();
        var send = client.SendAsync(request, cancellation.Token);
        await administration.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            using var response = await send;
            Assert.Equal(499, (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
        }

        AuditEvent? audit = null;
        for (var attempt = 0; attempt < 30 && audit is null; attempt++)
        {
            audit = (await factory.ReadAuditEventsAsync())
                .SingleOrDefault(candidate => candidate.CorrelationId == correlationId);
            if (audit is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(audit);
        Assert.Equal("allowed", audit!.Outcome);
        Assert.Contains("request_cancelled", audit.DetailJson, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
        Assert.Single(
            await factory.ReadAuditEventsAsync(),
            candidate => candidate.CorrelationId == correlationId);
    }

    private static IdentityWebApplicationFactory CreateFactory(
        IdentitySqlServerTestDatabase database,
        Action<IServiceCollection>? configureServices = null) =>
        new(
            database,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Forms:Enabled"] = "true",
                ["Forms:FingerprintKey"] =
                    Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
                ["Forms:DuplicateWindow"] = "00:15:00",
                ["Forms:RateLimit:PermitLimit"] = "100",
                ["Forms:RateLimit:Window"] = "00:05:00",
                ["Forms:RateLimit:Retention"] = "1.00:00:00",
                ["Forms:RetentionPeriod"] = "90.00:00:00",
                ["Forms:MaximumRequestBytes"] = "64000",
                ["Forms:MaximumFieldCount"] = "50",
                ["Forms:MaximumFieldValueLength"] = "4000",
                ["Forms:MaximumTotalValueLength"] = "16000",
                ["Forms:Delivery:Mode"] = "Disabled",
            },
            configureServices: configureServices);

    private static async Task SeedDefinitionAsync(IdentityWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var definition = new FormDefinition("test-contact-v1", "Synthetic contact");
        context.Add(definition);
        await context.SaveChangesAsync();
        var version = new FormDefinitionVersion(
            definition.Id,
            1,
            "test-destination",
            "test-template",
            "test-consent-v1",
            Now);
        context.Add(version);
        context.Add(new FormField(
            version.Id,
            "email",
            "Email",
            FormFieldKind.Email,
            true,
            3,
            320,
            null,
            null,
            FormPatternKind.Email,
            [],
            1,
            FormPrivacyClass.Contact,
            Now));
        await context.SaveChangesAsync();
        definition.Publish(version);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task<Guid> SeedDeadLetteredSubmissionAsync(
        IdentityWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IActiveFormDefinitionReader>();
        var definition = await reader.GetActiveAsync(
            "test-contact-v1",
            CancellationToken.None);
        Assert.True(definition.IsSuccess);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["email"] = "synthetic-current@example.test",
        };
        var acceptedAt = DateTimeOffset.UtcNow.AddDays(-100);
        var submitted = await scope.ServiceProvider
            .GetRequiredService<IFormSubmissionStore>()
            .SubmitAsync(
                new ValidatedFormSubmission(
                    definition.Success,
                    values,
                    FormPayloadCanonicalizer.ComputeHash(
                        values,
                        definition.Success.ConsentVersion),
                    Enumerable.Repeat((byte)42, 32).ToArray(),
                    "forms-current-seed",
                    acceptedAt),
                CancellationToken.None);
        Assert.True(submitted.IsSuccess);

        var jobStore = scope.ServiceProvider.GetRequiredService<IDurableJobStore>();
        var acquired = await jobStore.TryAcquireNextAsync(
            new WorkerIdentity("forms-current-seed-worker"),
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.NotNull(acquired.Success.Job);
        var deadLettered = await jobStore.FailAsync(
            acquired.Success.Job.JobInstanceId,
            acquired.Success.Job.LeaseToken,
            "synthetic_delivery_failure",
            nextRunAtUtc: null,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.True(deadLettered.IsSuccess);
        return submitted.Success.SubmissionId;
    }

    private static IReadOnlyList<AdminOperation> CreateAdminOperations(
        Guid submissionId,
        string correlationPrefix) =>
        [
            new(
                HttpMethod.Get,
                "/admin/forms/definitions/test-contact-v1",
                Body: null,
                FormAdministrationService.DefinitionReadAction,
                $"{correlationPrefix}-definition-read"),
            new(
                HttpMethod.Get,
                "/admin/forms/submissions?take=10",
                Body: null,
                FormAdministrationService.SubmissionReadAction,
                $"{correlationPrefix}-submission-read"),
            CreatePublishOperation(correlationPrefix, new byte[8]),
            CreateRetryOperation(submissionId, correlationPrefix),
            CreateRetentionOperation(
                submissionId,
                correlationPrefix,
                FormRetentionAction.PlaceLegalHold,
                new byte[8]),
            CreateRetentionOperation(
                submissionId,
                correlationPrefix,
                FormRetentionAction.ReleaseLegalHold,
                new byte[8]),
            CreateRetentionOperation(
                submissionId,
                correlationPrefix,
                FormRetentionAction.MarkEligible,
                new byte[8]),
            CreateRetentionOperation(
                submissionId,
                correlationPrefix,
                FormRetentionAction.Anonymize,
                new byte[8]),
        ];

    private static AdminOperation CreatePublishOperation(
        string correlationPrefix,
        byte[] expectedStateRowVersion) =>
        new(
            HttpMethod.Post,
            "/admin/forms/definitions/test-contact-v1/publish",
            new
            {
                title = "Synthetic current definition",
                destinationKey = "test-destination",
                templateKey = "test-template",
                consentVersion = "test-consent-v2",
                fields = new[]
                {
                    new
                    {
                        key = "email",
                        label = "Email",
                        kind = "Email",
                        required = true,
                        minimumLength = 3,
                        maximumLength = 320,
                        minimumValue = (decimal?)null,
                        maximumValue = (decimal?)null,
                        patternKind = "Email",
                        choices = Array.Empty<string>(),
                        order = 1,
                        privacyClass = "Contact",
                    },
                },
                expectedStateRowVersion,
            },
            FormAdministrationService.DefinitionPublishAction,
            $"{correlationPrefix}-definition-publish");

    private static AdminOperation CreateRetryOperation(
        Guid submissionId,
        string correlationPrefix) =>
        new(
            HttpMethod.Post,
            $"/admin/forms/submissions/{submissionId:D}/delivery/retry",
            new { reason = "synthetic recovery" },
            FormAdministrationService.DeliveryRetryAction,
            $"{correlationPrefix}-delivery-retry");

    private static AdminOperation CreateRetentionOperation(
        Guid submissionId,
        string correlationPrefix,
        FormRetentionAction action,
        byte[] expectedStateRowVersion) =>
        new(
            HttpMethod.Post,
            $"/admin/forms/submissions/{submissionId:D}/retention",
            new
            {
                action = action.ToString(),
                expectedStateRowVersion,
            },
            FormAdministrationService.RetentionChangeAction,
            $"{correlationPrefix}-retention-{action.ToString().ToLowerInvariant()}");

    private static async Task<HttpResponseMessage> SendAdminOperationAsync(
        HttpClient client,
        AdminOperation operation,
        string? antiforgeryToken)
    {
        using var request = new HttpRequestMessage(operation.Method, operation.RequestUri);
        if (operation.Body is not null)
        {
            request.Content = JsonContent.Create(operation.Body);
        }

        request.Headers.Add("X-Correlation-ID", operation.CorrelationId);
        if (!string.IsNullOrWhiteSpace(antiforgeryToken))
        {
            request.Headers.Add("RequestVerificationToken", antiforgeryToken);
        }

        return await client.SendAsync(request);
    }

    private static byte[] ReadEntityTag(HttpResponseMessage response)
    {
        var tag = response.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(tag));
        return Convert.FromBase64String(tag!.Trim('"'));
    }

    private static async Task<byte[]> ReadSubmissionRowVersionAsync(
        IdentityWebApplicationFactory factory,
        Guid submissionId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        return await context.Set<FormSubmission>()
            .AsNoTracking()
            .Where(submission => submission.Id == submissionId)
            .Select(submission => EF.Property<byte[]>(
                submission,
                PersistencePropertyNames.RowVersion))
            .SingleAsync();
    }

    private static async Task<IReadOnlyList<HttpClient>> CreateAuthenticatedClientsAsync(
        IdentityWebApplicationFactory factory,
        SeededIdentityUser user,
        int count)
    {
        var clients = new List<HttpClient>(count);
        for (var index = 0; index < count; index++)
        {
            clients.Add(await factory.CreateAuthenticatedClientAsync(
                user,
                [RoleNames.SiteAdministrator],
                mfaSatisfied: true));
        }

        return clients;
    }

    private static async Task<IReadOnlyList<HttpClient>> CreateClientsWithoutSecurityStampAsync(
        IdentityWebApplicationFactory factory,
        SeededIdentityUser seededUser,
        int count)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<HusayniaIdentityUser>>();
        var user = await userManager.FindByIdAsync(seededUser.UserId);
        Assert.NotNull(user);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, seededUser.UserId),
            new Claim(ClaimTypes.Name, user!.UserName ?? seededUser.Email),
            new Claim(ClaimTypes.Role, RoleNames.SiteAdministrator),
            new Claim("amr", "mfa"),
            new Claim("husaynia.mfa", "true"),
        };
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme));
        var options = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var protectedTicket = options.TicketDataFormat.Protect(
            new AuthenticationTicket(
                principal,
                IdentityConstants.ApplicationScheme));
        var clients = new List<HttpClient>(count);
        for (var index = 0; index < count; index++)
        {
            var client = factory.CreateIdentityClient();
            client.DefaultRequestHeaders.Add(
                "Cookie",
                $"{options.Cookie.Name}={protectedTicket}");
            clients.Add(client);
        }

        return clients;
    }

    private static async Task InvalidateSessionAsync(
        IdentityWebApplicationFactory factory,
        string userId,
        StaleSessionKind staleSessionKind)
    {
        if (staleSessionKind == StaleSessionKind.MissingSecurityStamp)
        {
            return;
        }

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<HusayniaIdentityUser>>();
        var user = await userManager.FindByIdAsync(userId);
        Assert.NotNull(user);
        IdentityResult result;
        switch (staleSessionKind)
        {
            case StaleSessionKind.Disabled:
                user!.Disable(DateTimeOffset.UtcNow);
                result = await userManager.UpdateAsync(user);
                break;
            case StaleSessionKind.RevokedAdministrator:
                result = await userManager.RemoveFromRoleAsync(
                    user!,
                    RoleNames.SiteAdministrator);
                break;
            case StaleSessionKind.ChangedSecurityStamp:
                result = await userManager.UpdateSecurityStampAsync(user!);
                break;
            case StaleSessionKind.DeletedUser:
                result = await userManager.DeleteAsync(user!);
                break;
            case StaleSessionKind.RevokedMfa:
                result = await userManager.SetTwoFactorEnabledAsync(user!, enabled: false);
                break;
            default:
                throw new InvalidOperationException("Unsupported stale-session scenario.");
        }

        Assert.True(
            result.Succeeded,
            string.Join(", ", result.Errors.Select(error => error.Code)));
    }

    private static SubmitFormRequest ValidRequest() =>
        new(
            "test-contact-v1",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["email"] = "person@example.test",
            },
            "test-consent-v1",
            string.Empty);

    private static async Task<string> GetFormsAntiforgeryTokenAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<AntiforgeryResponse>(
            "/forms/antiforgery");
        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response!.RequestToken));
        return response.RequestToken;
    }

    private static async Task<HttpResponseMessage> PostWithTokenAsync<T>(
        HttpClient client,
        string requestUri,
        T value,
        string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(value),
        };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendRawWithTokenAsync(
        HttpClient client,
        string requestUri,
        string body,
        string token,
        string correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("X-Correlation-ID", correlationId);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendGetWithCorrelationAsync(
        HttpClient client,
        string requestUri,
        string correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("X-Correlation-ID", correlationId);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendThrowingBodyRequestAsync(
        HttpClient client,
        string correlationId,
        string antiforgeryToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/forms/definitions/test-contact-v1/publish")
        {
            Content = new StringContent(
                CreateValidPublishJson(),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("RequestVerificationToken", antiforgeryToken);
        request.Headers.Add("X-Correlation-ID", correlationId);
        request.Headers.Add(ThrowingBodyStartupFilter.HeaderName, "true");
        return await client.SendAsync(request);
    }

    private static string CreateValidPublishJson() =>
        """
        {"title":"Synthetic","destinationKey":"test-destination","templateKey":"test-template","consentVersion":"test","fields":[{"key":"email","label":"Email","kind":"Email","required":true,"minimumLength":3,"maximumLength":320,"minimumValue":null,"maximumValue":null,"patternKind":"Email","choices":[],"order":1,"privacyClass":"Contact"}],"expectedStateRowVersion":"AAAAAAAAAAA="}
        """;

    private static void ConfigureBoundaryFailure(
        IServiceCollection services,
        BoundaryFailure failure,
        FailureToggle userLookupFailure)
    {
        switch (failure)
        {
            case BoundaryFailure.Authentication:
                services.RemoveAll<IAuthenticationService>();
                services.AddSingleton<IAuthenticationService, ThrowingAuthenticationService>();
                break;
            case BoundaryFailure.UserLookup:
                services.RemoveAll<UserManager<HusayniaIdentityUser>>();
                services.AddSingleton(userLookupFailure);
                services.AddScoped<UserManager<HusayniaIdentityUser>>(provider =>
                    new ThrowingLookupUserManager(
                    provider.GetRequiredService<IUserStore<HusayniaIdentityUser>>(),
                    provider.GetRequiredService<IOptions<IdentityOptions>>(),
                    provider.GetRequiredService<IPasswordHasher<HusayniaIdentityUser>>(),
                    provider.GetServices<IUserValidator<HusayniaIdentityUser>>(),
                    provider.GetServices<IPasswordValidator<HusayniaIdentityUser>>(),
                    provider.GetRequiredService<ILookupNormalizer>(),
                    provider.GetRequiredService<IdentityErrorDescriber>(),
                    provider,
                    provider.GetRequiredService<ILogger<UserManager<HusayniaIdentityUser>>>(),
                    userLookupFailure));
                break;
            case BoundaryFailure.Policy:
                services.RemoveAll<IAuthorizationService>();
                services.AddSingleton<IAuthorizationService, ThrowingAuthorizationService>();
                break;
            case BoundaryFailure.Antiforgery:
                services.RemoveAll<IAntiforgery>();
                services.AddSingleton<IAntiforgery, ThrowingAntiforgery>();
                break;
            case BoundaryFailure.Body:
                services.AddSingleton<IStartupFilter, ThrowingBodyStartupFilter>();
                break;
        }
    }

    private static async Task AssertNoWorkflowAsync(
        IdentityWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HusayniaDbContext>();
        Assert.Equal(0, await context.Set<FormSubmission>().CountAsync());
        Assert.Equal(0, await context.Set<FormSubmissionValue>().CountAsync());
        Assert.Equal(0, await context.Set<Husaynia.Domain.Operations.Jobs.JobInstance>().CountAsync());
    }

    private sealed record SubmitFormRequest(
        string FormKey,
        Dictionary<string, string> Fields,
        string? ConsentVersion,
        string? Honeypot);

    private sealed record AntiforgeryResponse(string RequestToken);

    private sealed record FormSubmissionResponse(
        Guid SubmissionId,
        DateTimeOffset AcceptedAtUtc,
        string CorrelationId);

    private sealed record AdminOperation(
        HttpMethod Method,
        string RequestUri,
        object? Body,
        string Action,
        string CorrelationId);

    public enum StaleSessionKind
    {
        Disabled,
        RevokedAdministrator,
        ChangedSecurityStamp,
        DeletedUser,
        MissingSecurityStamp,
        RevokedMfa,
    }

    private sealed class ThrowingAuditFinalizer : IIdentityAuditFinalizer
    {
        internal int CallCount { get; private set; }

        public Task FinalizeOnceAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            Func<CancellationToken, Task>? completePersistenceAsync = null)
        {
            CallCount++;
            throw new IdentityAuditFinalizationException("synthetic audit failure");
        }
    }

    private sealed class ThrowingFormAdministration : IFormAdministration
    {
        public Task<Result<FormDefinitionView, FormError>> GetDefinitionAsync(
            string formKey,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic service failure");

        public Task<Result<FormDefinitionPublicationReceipt, FormError>> PublishAsync(
            PublishFormDefinitionCommand command,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic service failure");

        public Task<Result<IReadOnlyList<FormSubmissionSummary>, FormError>> ReadSubmissionsAsync(
            int take,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic service failure");

        public Task<Result<bool, FormError>> RetryDeliveryAsync(
            Guid submissionId,
            string reason,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic service failure");

        public Task<Result<FormRetentionReceipt, FormError>> ChangeRetentionAsync(
            FormRetentionCommand command,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic service failure");
    }

    private sealed class ThrowingFormAdministrationStore : IFormAdministrationStore
    {
        public Task<Result<FormDefinitionView, FormError>> GetDefinitionAsync(
            string formKey,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic store failure");

        public Task<Result<FormDefinitionPublicationReceipt, FormError>> PublishAsync(
            PublishFormDefinitionCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic store failure");

        public Task<Result<IReadOnlyList<FormSubmissionSummary>, FormError>> ReadSubmissionsAsync(
            int take,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic store failure");

        public Task<Result<bool, FormError>> RetryDeliveryAsync(
            Guid submissionId,
            string reason,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic store failure");

        public Task<Result<FormRetentionReceipt, FormError>> ChangeRetentionAsync(
            FormRetentionCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic store failure");
    }

    private sealed class CancellingFormAdministration : IFormAdministration
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<FormDefinitionView, FormError>> GetDefinitionAsync(
            string formKey,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public Task<Result<FormDefinitionPublicationReceipt, FormError>> PublishAsync(
            PublishFormDefinitionCommand command,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<FormSubmissionSummary>, FormError>> ReadSubmissionsAsync(
            int take,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, FormError>> RetryDeliveryAsync(
            Guid submissionId,
            string reason,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<FormRetentionReceipt, FormError>> ChangeRetentionAsync(
            FormRetentionCommand command,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FailureToggle
    {
        internal bool Enabled { get; set; }
    }

    private sealed class ThrowingLookupUserManager(
        IUserStore<HusayniaIdentityUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<HusayniaIdentityUser> passwordHasher,
        IEnumerable<IUserValidator<HusayniaIdentityUser>> userValidators,
        IEnumerable<IPasswordValidator<HusayniaIdentityUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<HusayniaIdentityUser>> logger,
        FailureToggle failure)
        : UserManager<HusayniaIdentityUser>(
            store,
            optionsAccessor,
            passwordHasher,
            userValidators,
            passwordValidators,
            keyNormalizer,
            errors,
            services,
            logger)
    {
        public override Task<HusayniaIdentityUser?> FindByIdAsync(string userId) =>
            failure.Enabled
                ? throw new InvalidOperationException("synthetic user lookup failure")
                : base.FindByIdAsync(userId);
    }

    private sealed class ThrowingAuthenticationService : IAuthenticationService
    {
        private int calls;

        public Task<AuthenticateResult> AuthenticateAsync(
            HttpContext context,
            string? scheme) =>
            Interlocked.Increment(ref calls) % 2 == 1
                ? Task.FromResult(AuthenticateResult.NoResult())
                : throw new InvalidOperationException("synthetic authentication failure");

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            throw new InvalidOperationException("synthetic authorization failure");

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName) =>
            throw new InvalidOperationException("synthetic authorization failure");
    }

    private sealed class ThrowingAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
            throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            throw new NotSupportedException();

        public Task ValidateRequestAsync(HttpContext httpContext) =>
            throw new InvalidOperationException("synthetic antiforgery failure");

        public void SetCookieTokenAndHeader(HttpContext httpContext) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingBodyStartupFilter : IStartupFilter
    {
        internal const string HeaderName = "X-Forms-Throw-Body";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(async (context, continuation) =>
                {
                    if (context.Request.Headers.ContainsKey(HeaderName))
                    {
                        context.Request.Body = new ThrowingReadStream();
                    }

                    await continuation();
                });
                next(application);
            };
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("synthetic body read failure");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(
                new IOException("synthetic body read failure"));

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    public enum BoundaryFailure
    {
        Authentication,
        UserLookup,
        Policy,
        Antiforgery,
        Body,
    }
}
