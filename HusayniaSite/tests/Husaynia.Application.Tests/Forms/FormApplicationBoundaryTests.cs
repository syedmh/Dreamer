using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Forms;
using Husaynia.Domain.Identity;

namespace Husaynia.Application.Tests.Forms;

public sealed class FormApplicationBoundaryTests
{
    [Fact]
    public async Task InvalidSubmissionNeverReachesAtomicStore()
    {
        var definition = CreateDefinition();
        var store = new RecordingSubmissionStore();
        var service = new FormSubmissionService(
            new FixedDefinitionReader(definition),
            store,
            new FormSubmissionValidator(new FormsOptions
            {
                Enabled = true,
            }),
            new FormsOptions
            {
                Enabled = true,
            },
            new FixedCorrelationContext(),
            TimeProvider.System);

        var result = await service.SubmitAsync(
            new FormSubmissionCommand(
                definition.FormKey,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["email"] = "invalid",
                },
                definition.ConsentVersion),
            new RequestFingerprint(Convert.ToBase64String(new byte[32])),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("validation_failed", result.Error.Code);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task AdminAuthorizationPrecedesCommandValidationAndStoreAccess()
    {
        var store = new RecordingAdministrationStore();
        var audit = new RecordingAuditFinalizer();
        var service = new FormAdministrationService(
            store,
            audit,
            new AdministrativeCapabilityAuthorizer());
        var actor = new AdministrativeRequestActor(
            true,
            "auditor",
            new HashSet<string>([RoleNames.ReadOnlyAuditor], StringComparer.Ordinal),
            true,
            "correlation-forms");

        var result = await service.PublishAsync(
            new PublishFormDefinitionCommand(
                "",
                "",
                "",
                "",
                null,
                [],
                new RowVersion(ReadOnlyMemory<byte>.Empty)),
            actor,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("forbidden", result.Error.Code);
        Assert.Equal(0, store.CallCount);
        var finalized = Assert.Single(audit.Events);
        Assert.Equal(PrivilegedAttemptOutcome.Denied, finalized.Outcome);
        Assert.Equal(FormAdministrationService.DefinitionPublishAction, finalized.Descriptor.Action);
    }

    [Fact]
    public void DeliveryPayloadRejectsUnknownDuplicateAndCaseWrongProperties()
    {
        var valid = new FormDeliveryJobPayload(Guid.NewGuid(), Guid.NewGuid()).Serialize();

        Assert.True(FormDeliveryJobPayload.TryParse(valid, out _));
        Assert.False(FormDeliveryJobPayload.TryParse(
            """{"SubmissionId":"00000000000000000000000000000000","definitionVersionId":"00000000000000000000000000000000"}""",
            out _));
        Assert.False(FormDeliveryJobPayload.TryParse(
            """{"submissionId":"00000000000000000000000000000000","submissionId":"00000000000000000000000000000000","definitionVersionId":"00000000000000000000000000000000"}""",
            out _));
        Assert.False(FormDeliveryJobPayload.TryParse(
            """{"submissionId":"00000000000000000000000000000000","definitionVersionId":"00000000000000000000000000000000","value":"secret"}""",
            out _));
    }

    [Fact]
    public async Task MissingOrMalformedConcurrencyTokensNeverReachAdministrationStore()
    {
        var store = new RecordingAdministrationStore();
        var audit = new RecordingAuditFinalizer();
        var service = new FormAdministrationService(
            store,
            audit,
            new AdministrativeCapabilityAuthorizer());
        var actor = new AdministrativeRequestActor(
            true,
            "site-admin",
            new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
            true,
            "forms-concurrency-validation");

        var publish = await service.PublishAsync(
            new PublishFormDefinitionCommand(
                "test-contact-v1",
                "Synthetic",
                "test-destination",
                "test-template",
                "test-consent-v1",
                [
                    new FormFieldDraft(
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
                        FormPrivacyClass.Contact),
                ],
                new RowVersion(new byte[7])),
            actor,
            CancellationToken.None);
        var retention = await service.ChangeRetentionAsync(
            new FormRetentionCommand(
                Guid.NewGuid(),
                FormRetentionAction.Anonymize,
                new RowVersion(ReadOnlyMemory<byte>.Empty)),
            actor with { CorrelationId = "forms-retention-token-validation" },
            CancellationToken.None);

        Assert.True(publish.IsFailure);
        Assert.True(retention.IsFailure);
        Assert.Equal(0, store.CallCount);
        Assert.Equal(2, audit.Events.Count);
        Assert.All(
            audit.Events,
            entry => Assert.Equal(PrivilegedAttemptOutcome.Allowed, entry.Outcome));
    }

    private static FormDefinitionView CreateDefinition()
    {
        var versionId = Guid.NewGuid();
        return new FormDefinitionView(
            Guid.NewGuid(),
            "test-contact-v1",
            "Synthetic contact",
            versionId,
            1,
            "test-consent-v1",
            [
                new FormFieldView(
                    Guid.NewGuid(),
                    versionId,
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
                    FormPrivacyClass.Contact),
            ],
            new RowVersion(new byte[8]));
    }

    private sealed class FixedDefinitionReader(FormDefinitionView definition)
        : IActiveFormDefinitionReader
    {
        public Task<Result<FormDefinitionView, FormError>> GetActiveAsync(
            string formKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<FormDefinitionView, FormError>(definition));
    }

    private sealed class RecordingSubmissionStore : IFormSubmissionStore
    {
        internal int CallCount { get; private set; }

        public Task<Result<FormReceipt, FormError>> SubmitAsync(
            ValidatedFormSubmission submission,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(
                Result.Succeed<FormReceipt, FormError>(
                    new FormReceipt(Guid.NewGuid(), DateTimeOffset.UtcNow)));
        }
    }

    private sealed class FixedCorrelationContext : ICorrelationContext
    {
        public CorrelationSnapshot Current { get; } =
            new("correlation-forms", "trace-forms", null);

        public IDisposable Begin(
            string? suppliedCorrelationId = null,
            string operationName = "operation") =>
            new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingAuditFinalizer : IIdentityAuditFinalizer
    {
        internal List<FinalizedAudit> Events { get; } = [];

        public Task FinalizeOnceAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            Func<CancellationToken, Task>? completePersistenceAsync = null)
        {
            Events.Add(new FinalizedAudit(descriptor, outcome));
            return Task.CompletedTask;
        }
    }

    private sealed record FinalizedAudit(
        IdentityAuditDescriptor Descriptor,
        PrivilegedAttemptOutcome Outcome);

    private sealed class RecordingAdministrationStore : IFormAdministrationStore
    {
        internal int CallCount { get; private set; }

        public Task<Result<FormDefinitionView, FormError>> GetDefinitionAsync(
            string formKey,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new NotSupportedException();
        }

        public Task<Result<FormDefinitionPublicationReceipt, FormError>> PublishAsync(
            PublishFormDefinitionCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<FormSubmissionSummary>, FormError>> ReadSubmissionsAsync(
            int take,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new NotSupportedException();
        }

        public Task<Result<bool, FormError>> RetryDeliveryAsync(
            Guid submissionId,
            string reason,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new NotSupportedException();
        }

        public Task<Result<FormRetentionReceipt, FormError>> ChangeRetentionAsync(
            FormRetentionCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new NotSupportedException();
        }
    }
}
