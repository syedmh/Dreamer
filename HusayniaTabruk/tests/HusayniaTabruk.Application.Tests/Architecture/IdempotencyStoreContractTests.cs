using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Tests.Architecture;

public sealed class IdempotencyStoreContractTests
{
    [Fact]
    public async Task CreateThenDuplicateCreateReturnsExistingProcessing()
    {
        ProbeIdempotencyStore store = new();
        IdempotencyCreateRequest request = CreateCreateRequest();

        IdempotencyCreateResult created = await store.TryCreateProcessingAsync(request);
        IdempotencyCreateResult duplicate = await store.TryCreateProcessingAsync(request);

        Assert.Equal(IdempotencyCreateOutcome.Created, created.Outcome);
        Assert.Equal(IdempotencyStatus.Processing, created.Receipt.Status);
        Assert.Null(created.Receipt.ResultReference);
        Assert.Equal(IdempotencyCreateOutcome.ExistingProcessing, duplicate.Outcome);
        Assert.Equal(created.Receipt, duplicate.Receipt);
    }

    [Fact]
    public async Task CreateAfterCompletionReturnsExistingCompleted()
    {
        ProbeIdempotencyStore store = new();
        IdempotencyCreateRequest createRequest = CreateCreateRequest();

        await store.TryCreateProcessingAsync(createRequest);
        IdempotencyTransitionResult completed = await store.TryCompleteAsync(
            CreateRequest(createRequest),
            "signup-123");

        IdempotencyCreateResult duplicate = await store.TryCreateProcessingAsync(
            CreateCreateRequest(
                organizationId: createRequest.OrganizationId,
                membershipId: createRequest.MembershipId,
                key: createRequest.Key,
                createdAt: createRequest.CreatedAt.AddMinutes(1),
                expiresAt: createRequest.ExpiresAt.AddMinutes(1)));

        Assert.Equal(IdempotencyTransitionOutcome.Completed, completed.Outcome);
        Assert.Equal(IdempotencyCreateOutcome.ExistingCompleted, duplicate.Outcome);
        Assert.Equal(completed.Receipt, duplicate.Receipt);
    }

    [Theory]
    [InlineData("signup.cancel", 'a')]
    [InlineData("signup.submit", 'b')]
    public async Task DifferentOperationOrFingerprintReturnsRequestMismatch(string operation, char fingerprintCharacter)
    {
        ProbeIdempotencyStore store = new();
        IdempotencyCreateRequest original = CreateCreateRequest();

        await store.TryCreateProcessingAsync(original);
        IdempotencyCreateResult mismatch = await store.TryCreateProcessingAsync(
            CreateCreateRequest(
                organizationId: original.OrganizationId,
                membershipId: original.MembershipId,
                key: original.Key,
                operation: operation,
                fingerprint: Fingerprint(fingerprintCharacter),
                createdAt: original.ExpiresAt,
                expiresAt: original.ExpiresAt.AddMinutes(1)));

        Assert.Equal(IdempotencyCreateOutcome.RequestMismatch, mismatch.Outcome);
        Assert.Equal(original.Operation, mismatch.Receipt.Operation);
        Assert.Equal(original.RequestFingerprint, mismatch.Receipt.RequestFingerprint);
    }

    [Fact]
    public async Task CreateAtExpiryReturnsExpiredWithoutOverwritingTheStoredReceipt()
    {
        ProbeIdempotencyStore store = new();
        DateTimeOffset createdAt = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        IdempotencyCreateRequest original = CreateCreateRequest(
            createdAt: createdAt,
            expiresAt: createdAt.AddMinutes(5));

        IdempotencyCreateResult created = await store.TryCreateProcessingAsync(original);
        IdempotencyCreateResult expired = await store.TryCreateProcessingAsync(
            CreateCreateRequest(
                organizationId: original.OrganizationId,
                membershipId: original.MembershipId,
                key: original.Key,
                createdAt: original.ExpiresAt,
                expiresAt: original.ExpiresAt.AddMinutes(10)));

        Assert.Equal(IdempotencyCreateOutcome.Expired, expired.Outcome);
        Assert.Equal(created.Receipt, expired.Receipt);
    }

    [Fact]
    public async Task ConcurrentTerminalTransitionsProduceOneWinnerAndOneExpectedStatusMismatch()
    {
        ProbeIdempotencyStore store = new();
        IdempotencyCreateRequest createRequest = CreateCreateRequest();

        await store.TryCreateProcessingAsync(createRequest);
        IdempotencyRequest request = CreateRequest(createRequest);

        Task<IdempotencyTransitionResult> completeTask = Task.Run(async () =>
            await store.TryCompleteAsync(request, "signup-123"));
        Task<IdempotencyTransitionResult> failTask = Task.Run(async () =>
            await store.TryFailAsync(request));

        IdempotencyTransitionResult[] results = await Task.WhenAll(completeTask, failTask);

        IdempotencyTransitionResult winner = Assert.Single(
            results,
            result => result.Outcome is IdempotencyTransitionOutcome.Completed or IdempotencyTransitionOutcome.Failed);
        IdempotencyTransitionResult loser = Assert.Single(
            results,
            result => result.Outcome == IdempotencyTransitionOutcome.ExpectedStatusMismatch);

        Assert.NotNull(winner.Receipt);
        Assert.Equal(winner.Receipt, loser.Receipt);
        Assert.NotEqual(IdempotencyStatus.Processing, winner.Receipt!.Status);
    }

    [Fact]
    public async Task TerminalTransitionAfterTerminalStateReturnsExpectedStatusMismatch()
    {
        ProbeIdempotencyStore store = new();
        IdempotencyCreateRequest createRequest = CreateCreateRequest();

        await store.TryCreateProcessingAsync(createRequest);
        IdempotencyTransitionResult first = await store.TryFailAsync(CreateRequest(createRequest));
        IdempotencyTransitionResult second = await store.TryCompleteAsync(
            CreateRequest(createRequest),
            "signup-123");

        Assert.Equal(IdempotencyTransitionOutcome.Failed, first.Outcome);
        Assert.Equal(IdempotencyTransitionOutcome.ExpectedStatusMismatch, second.Outcome);
        Assert.Equal(first.Receipt, second.Receipt);
    }

    [Fact]
    public async Task MissingTransitionReturnsMissingWithoutReceipt()
    {
        ProbeIdempotencyStore store = new();
        IdempotencyTransitionResult missing = await store.TryCompleteAsync(
            CreateRequest(CreateCreateRequest()),
            "signup-123");

        Assert.Equal(IdempotencyTransitionOutcome.Missing, missing.Outcome);
        Assert.Null(missing.Receipt);
    }

    [Fact]
    public async Task OperationsHonorCancellation()
    {
        ProbeIdempotencyStore store = new();
        IdempotencyCreateRequest createRequest = CreateCreateRequest();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.TryCreateProcessingAsync(createRequest, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.TryFailAsync(CreateRequest(createRequest), cancellation.Token));
    }

    private static RequestFingerprint Fingerprint(char character) =>
        RequestFingerprint.FromSha256(new string(character, 64));

    private static IdempotencyCreateRequest CreateCreateRequest(
        OrganizationId? organizationId = null,
        MembershipId? membershipId = null,
        IdempotencyKey? key = null,
        string operation = "signup.submit",
        RequestFingerprint? fingerprint = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null)
    {
        DateTimeOffset actualCreatedAt = createdAt ?? new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        return new IdempotencyCreateRequest(
            organizationId ?? OrganizationId.New(),
            membershipId ?? MembershipId.New(),
            key ?? IdempotencyKey.New(),
            operation,
            fingerprint ?? Fingerprint('a'),
            actualCreatedAt,
            expiresAt ?? actualCreatedAt.AddMinutes(10));
    }

    private static IdempotencyRequest CreateRequest(
        IdempotencyCreateRequest request,
        string? operation = null,
        RequestFingerprint? fingerprint = null) =>
        new(
            request.OrganizationId,
            request.MembershipId,
            request.Key,
            operation ?? request.Operation,
            fingerprint ?? request.RequestFingerprint);

    private sealed class ProbeIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<(OrganizationId OrganizationId, MembershipId MembershipId, IdempotencyKey Key), IdempotencyReceipt> _receipts = [];
        private readonly Lock _gate = new();

        public ValueTask<IdempotencyReceipt?> FindAsync(
            OrganizationId organizationId,
            MembershipId membershipId,
            IdempotencyKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                return ValueTask.FromResult(
                    _receipts.GetValueOrDefault((organizationId, membershipId, key)));
            }
        }

        public ValueTask<IdempotencyCreateResult> TryCreateProcessingAsync(
            IdempotencyCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(request);

            lock (_gate)
            {
                var storageKey = (request.OrganizationId, request.MembershipId, request.Key);
                if (_receipts.TryGetValue(storageKey, out IdempotencyReceipt? existing))
                {
                    return ValueTask.FromResult(MapCreateOutcome(existing, request));
                }

                IdempotencyReceipt created = new(
                    request.OrganizationId,
                    request.MembershipId,
                    request.Key,
                    request.Operation,
                    request.RequestFingerprint,
                    IdempotencyStatus.Processing,
                    resultReference: null,
                    request.CreatedAt,
                    request.ExpiresAt);

                _receipts.Add(storageKey, created);
                return ValueTask.FromResult(new IdempotencyCreateResult(IdempotencyCreateOutcome.Created, created));
            }
        }

        public ValueTask<IdempotencyTransitionResult> TryCompleteAsync(
            IdempotencyRequest request,
            string resultReference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(resultReference);

            return TransitionAsync(request, IdempotencyStatus.Completed, resultReference);
        }

        public ValueTask<IdempotencyTransitionResult> TryFailAsync(
            IdempotencyRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(request);

            return TransitionAsync(request, IdempotencyStatus.Failed, resultReference: null);
        }

        private ValueTask<IdempotencyTransitionResult> TransitionAsync(
            IdempotencyRequest request,
            IdempotencyStatus nextStatus,
            string? resultReference)
        {
            lock (_gate)
            {
                var storageKey = (request.OrganizationId, request.MembershipId, request.Key);
                if (!_receipts.TryGetValue(storageKey, out IdempotencyReceipt? existing))
                {
                    return ValueTask.FromResult(new IdempotencyTransitionResult(IdempotencyTransitionOutcome.Missing, null));
                }

                if (!string.Equals(existing.Operation, request.Operation, StringComparison.Ordinal)
                    || existing.RequestFingerprint != request.RequestFingerprint)
                {
                    return ValueTask.FromResult(new IdempotencyTransitionResult(
                        IdempotencyTransitionOutcome.RequestMismatch,
                        existing));
                }

                if (existing.Status != IdempotencyStatus.Processing)
                {
                    return ValueTask.FromResult(new IdempotencyTransitionResult(
                        IdempotencyTransitionOutcome.ExpectedStatusMismatch,
                        existing));
                }

                IdempotencyReceipt transitioned = new(
                    request.OrganizationId,
                    request.MembershipId,
                    request.Key,
                    request.Operation,
                    request.RequestFingerprint,
                    nextStatus,
                    resultReference,
                    existing.CreatedAt,
                    existing.ExpiresAt);

                _receipts[storageKey] = transitioned;

                return ValueTask.FromResult(new IdempotencyTransitionResult(
                    nextStatus == IdempotencyStatus.Completed
                        ? IdempotencyTransitionOutcome.Completed
                        : IdempotencyTransitionOutcome.Failed,
                    transitioned));
            }
        }

        private static IdempotencyCreateResult MapCreateOutcome(
            IdempotencyReceipt existing,
            IdempotencyCreateRequest request) =>
            existing.Evaluate(request.Operation, request.RequestFingerprint, request.CreatedAt) switch
            {
                IdempotencyReceiptDisposition.Processing => new IdempotencyCreateResult(
                    IdempotencyCreateOutcome.ExistingProcessing,
                    existing),
                IdempotencyReceiptDisposition.Completed => new IdempotencyCreateResult(
                    IdempotencyCreateOutcome.ExistingCompleted,
                    existing),
                IdempotencyReceiptDisposition.Failed => new IdempotencyCreateResult(
                    IdempotencyCreateOutcome.ExistingFailed,
                    existing),
                IdempotencyReceiptDisposition.RequestMismatch => new IdempotencyCreateResult(
                    IdempotencyCreateOutcome.RequestMismatch,
                    existing),
                IdempotencyReceiptDisposition.Expired => new IdempotencyCreateResult(
                    IdempotencyCreateOutcome.Expired,
                    existing),
                _ => throw new InvalidOperationException("Unsupported idempotency receipt disposition."),
            };
    }
}
