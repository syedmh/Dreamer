using System.Reflection;
using HusayniaTabruk.Application.Abstractions;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;

namespace HusayniaTabruk.Application.Tests.Architecture;

public sealed class ApplicationPortTests
{
    [Fact]
    public void CurrentActorExposesOnlyDatabaseBackedIdentityScope()
    {
        Assert.Equal(
            [nameof(ICurrentActor.UserId), nameof(ICurrentActor.MembershipId), nameof(ICurrentActor.OrganizationId)],
            typeof(ICurrentActor).GetProperties().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void RequiredCrossCuttingPortsAreInterfaces()
    {
        Type[] ports =
        [
            typeof(IClock),
            typeof(ICurrentActor),
            typeof(IUnitOfWork),
            typeof(IIdentityService),
            typeof(ITokenService),
            typeof(IMembershipRepository),
            typeof(IServiceDateRepository),
            typeof(ISignupRepository),
            typeof(IThreadRepository),
            typeof(INotificationRepository),
            typeof(IIdempotencyStore),
            typeof(IAuditWriter),
            typeof(IPrivilegedAccessWriter),
            typeof(IOutboxWriter),
            typeof(IPushGateway),
            typeof(IStepUpVerifier),
        ];

        Assert.All(ports, port => Assert.True(port.IsInterface, $"{port.Name} must remain an interface."));
    }

    [Fact]
    public void NotificationReadPortRequiresOrganizationRecipientAndNotificationScope()
    {
        MethodInfo method = Assert.Single(typeof(INotificationRepository).GetMethods());
        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(nameof(INotificationRepository.GetAsync), method.Name);
        Assert.Equal(
            [
                typeof(OrganizationId),
                typeof(MembershipId),
                typeof(NotificationId),
                typeof(CancellationToken),
            ],
            parameters.Select(parameter => parameter.ParameterType).ToArray());
        Assert.Equal(
            [
                "organizationId",
                "recipientMembershipId",
                "notificationId",
                "cancellationToken",
            ],
            parameters.Select(parameter => parameter.Name!).ToArray());
        Assert.True(parameters[^1].HasDefaultValue);
        Assert.Equal(typeof(ValueTask<Result<Notification>>), method.ReturnType);
    }

    [Fact]
    public void DependencyUnavailableExceptionIsProviderNeutralAndFrozen()
    {
        Type exceptionType = typeof(DependencyUnavailableException);
        ConstructorInfo constructor = Assert.Single(exceptionType.GetConstructors());
        ParameterInfo parameter = Assert.Single(constructor.GetParameters());
        InvalidOperationException innerException = new("provider diagnostic sentinel");
        DependencyUnavailableException exception = new(innerException);

        Assert.True(exceptionType.IsSealed);
        Assert.Equal(typeof(Exception), exceptionType.BaseType);
        Assert.Equal(typeof(Exception), parameter.ParameterType);
        Assert.True(parameter.HasDefaultValue);
        Assert.Null(parameter.DefaultValue);
        Assert.Equal("A required dependency is temporarily unavailable.", exception.Message);
        Assert.Same(innerException, exception.InnerException);
        Assert.Empty(
            exceptionType.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.DoesNotContain(
            typeof(DependencyUnavailableException).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void StepUpContractIsPurposeBoundAndSingleUse()
    {
        Assert.Equal(
            [nameof(IStepUpVerifier.IssueAsync), nameof(IStepUpVerifier.ConsumeAsync)],
            typeof(IStepUpVerifier).GetMethods().Select(method => method.Name).ToArray());
    }

    [Fact]
    public void CurrentActorUsesStronglyTypedIds()
    {
        Assert.Equal(typeof(UserId), typeof(ICurrentActor).GetProperty(nameof(ICurrentActor.UserId))?.PropertyType);
        Assert.Equal(typeof(MembershipId), typeof(ICurrentActor).GetProperty(nameof(ICurrentActor.MembershipId))?.PropertyType);
        Assert.Equal(typeof(OrganizationId), typeof(ICurrentActor).GetProperty(nameof(ICurrentActor.OrganizationId))?.PropertyType);
    }

    [Fact]
    public void IdempotencyReceiptDefinesLifecycleExpiryAndMismatchSemantics()
    {
        DateTimeOffset createdAt = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset expiresAt = createdAt.AddDays(1);
        RequestFingerprint mismatchedFingerprint = RequestFingerprint.FromSha256(new string('b', 64));
        IdempotencyReceipt receipt = new(
            OrganizationId.New(),
            MembershipId.New(),
            IdempotencyKey.New(),
            "signup.submit",
            RequestFingerprint.FromSha256(new string('a', 64)),
            IdempotencyStatus.Processing,
            resultReference: null,
            createdAt,
            expiresAt);

        Assert.Equal(
            IdempotencyReceiptDisposition.Processing,
            receipt.Evaluate(receipt.Operation, receipt.RequestFingerprint, createdAt.AddHours(1)));
        Assert.Equal(
            IdempotencyReceiptDisposition.RequestMismatch,
            receipt.Evaluate(
                "signup.cancel",
                receipt.RequestFingerprint,
                createdAt.AddHours(1)));
        Assert.Equal(
            IdempotencyReceiptDisposition.RequestMismatch,
            receipt.Evaluate(
                receipt.Operation,
                mismatchedFingerprint,
                createdAt.AddHours(1)));
        Assert.Equal(
            IdempotencyReceiptDisposition.RequestMismatch,
            receipt.Evaluate(
                "signup.cancel",
                receipt.RequestFingerprint,
                receipt.ExpiresAt));
        Assert.Equal(
            IdempotencyReceiptDisposition.RequestMismatch,
            receipt.Evaluate(
                receipt.Operation,
                mismatchedFingerprint,
                receipt.ExpiresAt));
        Assert.Equal(
            IdempotencyReceiptDisposition.Expired,
            receipt.Evaluate(receipt.Operation, receipt.RequestFingerprint, receipt.ExpiresAt));
    }

    [Fact]
    public void IdempotencyLifecycleDtosRejectInvalidArgumentsAndFreezeOutcomeNames()
    {
        DateTimeOffset createdAt = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset expiresAt = createdAt.AddDays(1);
        OrganizationId organizationId = OrganizationId.New();
        MembershipId membershipId = MembershipId.New();
        IdempotencyKey key = IdempotencyKey.New();
        RequestFingerprint fingerprint = RequestFingerprint.FromSha256(new string('d', 64));

        IdempotencyCreateRequest createRequest = new(
            organizationId,
            membershipId,
            key,
            "signup.submit",
            fingerprint,
            createdAt,
            expiresAt);

        Assert.Equal("signup.submit", createRequest.Operation);
        Assert.Equal(fingerprint, createRequest.RequestFingerprint);
        Assert.Equal(createdAt, createRequest.CreatedAt);
        Assert.Equal(expiresAt, createRequest.ExpiresAt);

        IdempotencyRequest request = new(
            organizationId,
            membershipId,
            key,
            "signup.submit",
            fingerprint);

        Assert.Equal(key, request.Key);
        Assert.Equal(fingerprint, request.RequestFingerprint);

        IdempotencyReceipt processingReceipt = new(
            organizationId,
            membershipId,
            key,
            "signup.submit",
            fingerprint,
            IdempotencyStatus.Processing,
            resultReference: null,
            createdAt,
            expiresAt);

        IdempotencyReceipt completedReceipt = new(
            organizationId,
            membershipId,
            key,
            "signup.submit",
            fingerprint,
            IdempotencyStatus.Completed,
            "signup-123",
            createdAt,
            expiresAt);

        IdempotencyCreateResult createResult = new(IdempotencyCreateOutcome.Created, processingReceipt);
        IdempotencyTransitionResult completedResult = new(IdempotencyTransitionOutcome.Completed, completedReceipt);
        IdempotencyTransitionResult missingResult = new(IdempotencyTransitionOutcome.Missing, null);

        Assert.Equal(IdempotencyCreateOutcome.Created, createResult.Outcome);
        Assert.Equal(IdempotencyTransitionOutcome.Completed, completedResult.Outcome);
        Assert.Null(missingResult.Receipt);

        Assert.Equal(
            ["Created", "ExistingProcessing", "ExistingCompleted", "ExistingFailed", "RequestMismatch", "Expired"],
            Enum.GetNames<IdempotencyCreateOutcome>());
        Assert.Equal(
            ["Completed", "Failed", "Missing", "RequestMismatch", "ExpectedStatusMismatch"],
            Enum.GetNames<IdempotencyTransitionOutcome>());

        Assert.Throws<InvalidOperationException>(
            () => new IdempotencyCreateRequest(
                default,
                membershipId,
                key,
                "signup.submit",
                fingerprint,
                createdAt,
                expiresAt));
        Assert.Throws<InvalidOperationException>(
            () => new IdempotencyRequest(
                organizationId,
                default,
                key,
                "signup.submit",
                fingerprint));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IdempotencyCreateRequest(
                organizationId,
                membershipId,
                key,
                "signup.submit",
                fingerprint,
                createdAt,
                createdAt));
        Assert.Throws<ArgumentException>(
            () => new IdempotencyRequest(
                organizationId,
                membershipId,
                key,
                string.Empty,
                fingerprint));
        Assert.Throws<ArgumentNullException>(
            () => new IdempotencyRequest(
                organizationId,
                membershipId,
                key,
                "signup.submit",
                null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IdempotencyCreateResult((IdempotencyCreateOutcome)999, processingReceipt));
        Assert.Throws<ArgumentNullException>(
            () => new IdempotencyCreateResult(IdempotencyCreateOutcome.Created, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IdempotencyTransitionResult((IdempotencyTransitionOutcome)999, processingReceipt));
        Assert.Throws<ArgumentNullException>(
            () => new IdempotencyTransitionResult(IdempotencyTransitionOutcome.Completed, null));
        Assert.Throws<ArgumentException>(
            () => new IdempotencyTransitionResult(IdempotencyTransitionOutcome.Missing, processingReceipt));
    }

    [Fact]
    public void AuditContractsContainImmutableContextAndRejectEmptyIdentifiers()
    {
        Assert.Equal(
            ["Support", "Moderation", "Safeguarding"],
            Enum.GetNames<PrivilegedAccessPurpose>());

        AuditEntry audit = new(
            AuditEventId.New(),
            OrganizationId.New(),
            MembershipId.New(),
            "signup.approved",
            "signup",
            SignupId.New().ToString(),
            "capacity available",
            "operations",
            "case-123",
            """{"status":"Pending"}""",
            """{"status":"Approved"}""",
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("operations", audit.Purpose);
        Assert.Equal("case-123", audit.CorrelationId);
        Assert.NotNull(audit.BeforeState);
        Assert.NotNull(audit.AfterState);

        PrivilegedAccessEntry access = new(
            AuditEventId.New(),
            OrganizationId.New(),
            MembershipId.New(),
            "thread",
            ThreadId.New().ToString(),
            "reported message review",
            PrivilegedAccessPurpose.Moderation,
            "case-456",
            "opaque-cursor",
            RequestFingerprint.FromSha256(new string('c', 64)),
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("opaque-cursor", access.PageCursor);
        Assert.Equal(new string('c', 64), access.PageHash.Value);

        Assert.Throws<InvalidOperationException>(
            () => new AuditEntry(
                AuditEventId.New(),
                default,
                MembershipId.New(),
                "signup.approved",
                "signup",
                SignupId.New().ToString(),
                "capacity available",
                "operations",
                "case-123",
                null,
                null,
                new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)));

        Assert.Throws<InvalidOperationException>(
            () => new OutboxMessage(
                default,
                OrganizationId.New(),
                "audit.created",
                "{}",
                new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)));
        Assert.Throws<InvalidOperationException>(
            () => new PushMessage(
                OrganizationId.New(),
                default,
                "A signup changed.",
                "signup",
                SignupId.New().ToString()));
    }
}
