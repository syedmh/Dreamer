using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public interface IUnitOfWork
{
    ValueTask<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<Result<T>>> operation,
        CancellationToken cancellationToken = default);
}

public partial interface IMembershipRepository;

public partial interface IServiceDateRepository;

public partial interface ISignupRepository;

public partial interface IThreadRepository;

public partial interface INotificationRepository
{
    ValueTask<Result<Notification>> GetAsync(
        OrganizationId organizationId,
        MembershipId recipientMembershipId,
        NotificationId notificationId,
        CancellationToken cancellationToken = default);
}

public interface IIdempotencyStore
{
    ValueTask<IdempotencyReceipt?> FindAsync(
        OrganizationId organizationId,
        MembershipId membershipId,
        IdempotencyKey key,
        CancellationToken cancellationToken = default);

    ValueTask<IdempotencyCreateResult> TryCreateProcessingAsync(
        IdempotencyCreateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IdempotencyTransitionResult> TryCompleteAsync(
        IdempotencyRequest request,
        string resultReference,
        CancellationToken cancellationToken = default);

    ValueTask<IdempotencyTransitionResult> TryFailAsync(
        IdempotencyRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record IdempotencyCreateRequest
{
    public IdempotencyCreateRequest(
        OrganizationId organizationId,
        MembershipId membershipId,
        IdempotencyKey key,
        string operation,
        RequestFingerprint requestFingerprint,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        OrganizationId = organizationId.EnsureValid();
        MembershipId = membershipId.EnsureValid();
        Key = key.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(requestFingerprint);

        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be after creation.");
        }

        Operation = operation;
        RequestFingerprint = requestFingerprint;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public OrganizationId OrganizationId { get; }
    public MembershipId MembershipId { get; }
    public IdempotencyKey Key { get; }
    public string Operation { get; }
    public RequestFingerprint RequestFingerprint { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
}

public sealed record IdempotencyRequest
{
    public IdempotencyRequest(
        OrganizationId organizationId,
        MembershipId membershipId,
        IdempotencyKey key,
        string operation,
        RequestFingerprint requestFingerprint)
    {
        OrganizationId = organizationId.EnsureValid();
        MembershipId = membershipId.EnsureValid();
        Key = key.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(requestFingerprint);

        Operation = operation;
        RequestFingerprint = requestFingerprint;
    }

    public OrganizationId OrganizationId { get; }
    public MembershipId MembershipId { get; }
    public IdempotencyKey Key { get; }
    public string Operation { get; }
    public RequestFingerprint RequestFingerprint { get; }
}

public enum IdempotencyCreateOutcome
{
    Created = 0,
    ExistingProcessing = 1,
    ExistingCompleted = 2,
    ExistingFailed = 3,
    RequestMismatch = 4,
    Expired = 5,
}

public sealed record IdempotencyCreateResult
{
    public IdempotencyCreateResult(IdempotencyCreateOutcome outcome, IdempotencyReceipt receipt)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        ArgumentNullException.ThrowIfNull(receipt);

        bool receiptStatusMatchesOutcome = outcome switch
        {
            IdempotencyCreateOutcome.Created or IdempotencyCreateOutcome.ExistingProcessing =>
                receipt.Status == IdempotencyStatus.Processing,
            IdempotencyCreateOutcome.ExistingCompleted =>
                receipt.Status == IdempotencyStatus.Completed,
            IdempotencyCreateOutcome.ExistingFailed =>
                receipt.Status == IdempotencyStatus.Failed,
            IdempotencyCreateOutcome.RequestMismatch or IdempotencyCreateOutcome.Expired => true,
            _ => false,
        };

        if (!receiptStatusMatchesOutcome)
        {
            throw new ArgumentException(
                $"Create outcome {outcome} cannot include a {receipt.Status} receipt.",
                nameof(receipt));
        }

        Outcome = outcome;
        Receipt = receipt;
    }

    public IdempotencyCreateOutcome Outcome { get; }
    public IdempotencyReceipt Receipt { get; }
}

public enum IdempotencyTransitionOutcome
{
    Completed = 0,
    Failed = 1,
    Missing = 2,
    RequestMismatch = 3,
    ExpectedStatusMismatch = 4,
}

public sealed record IdempotencyTransitionResult
{
    public IdempotencyTransitionResult(IdempotencyTransitionOutcome outcome, IdempotencyReceipt? receipt)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (outcome == IdempotencyTransitionOutcome.Missing)
        {
            if (receipt is not null)
            {
                throw new ArgumentException("Missing transitions cannot include a stored receipt.", nameof(receipt));
            }
        }
        else
        {
            ArgumentNullException.ThrowIfNull(receipt);

            bool receiptStatusMatchesOutcome = outcome switch
            {
                IdempotencyTransitionOutcome.Completed =>
                    receipt.Status == IdempotencyStatus.Completed,
                IdempotencyTransitionOutcome.Failed =>
                    receipt.Status == IdempotencyStatus.Failed,
                IdempotencyTransitionOutcome.RequestMismatch => true,
                IdempotencyTransitionOutcome.ExpectedStatusMismatch =>
                    receipt.Status is IdempotencyStatus.Completed or IdempotencyStatus.Failed,
                _ => false,
            };

            if (!receiptStatusMatchesOutcome)
            {
                throw new ArgumentException(
                    $"Transition outcome {outcome} cannot include a {receipt.Status} receipt.",
                    nameof(receipt));
            }
        }

        Outcome = outcome;
        Receipt = receipt;
    }

    public IdempotencyTransitionOutcome Outcome { get; }
    public IdempotencyReceipt? Receipt { get; }
}

public enum IdempotencyStatus
{
    Processing = 0,
    Completed = 1,
    Failed = 2,
}

public enum IdempotencyReceiptDisposition
{
    Processing = 0,
    Completed = 1,
    Failed = 2,
    RequestMismatch = 3,
    Expired = 4,
}

public sealed record RequestFingerprint
{
    private RequestFingerprint(string value) => Value = value;

    public string Value { get; }

    public static RequestFingerprint FromSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Request fingerprint must be a 64-character SHA-256 hexadecimal value.",
                nameof(value));
        }

        return new(value.ToLowerInvariant());
    }

    public override string ToString() => Value;
}

public sealed record IdempotencyReceipt
{
    public IdempotencyReceipt(
        OrganizationId organizationId,
        MembershipId membershipId,
        IdempotencyKey key,
        string operation,
        RequestFingerprint requestFingerprint,
        IdempotencyStatus status,
        string? resultReference,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        OrganizationId = organizationId.EnsureValid();
        MembershipId = membershipId.EnsureValid();
        Key = key.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(requestFingerprint);

        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be after creation.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (status == IdempotencyStatus.Completed && string.IsNullOrWhiteSpace(resultReference))
        {
            throw new ArgumentException("Completed receipts require a result reference.", nameof(resultReference));
        }

        if (status != IdempotencyStatus.Completed && resultReference is not null)
        {
            throw new ArgumentException("Only completed receipts may include a result reference.", nameof(resultReference));
        }

        Operation = operation;
        RequestFingerprint = requestFingerprint;
        Status = status;
        ResultReference = resultReference;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public OrganizationId OrganizationId { get; }
    public MembershipId MembershipId { get; }
    public IdempotencyKey Key { get; }
    public string Operation { get; }
    public RequestFingerprint RequestFingerprint { get; }
    public IdempotencyStatus Status { get; }
    public string? ResultReference { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }

    public IdempotencyReceiptDisposition Evaluate(
        string operation,
        RequestFingerprint requestFingerprint,
        DateTimeOffset evaluatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(requestFingerprint);

        if (!string.Equals(operation, Operation, StringComparison.Ordinal)
            || requestFingerprint != RequestFingerprint)
        {
            return IdempotencyReceiptDisposition.RequestMismatch;
        }

        if (evaluatedAt >= ExpiresAt)
        {
            return IdempotencyReceiptDisposition.Expired;
        }

        return Status switch
        {
            IdempotencyStatus.Processing => IdempotencyReceiptDisposition.Processing,
            IdempotencyStatus.Completed => IdempotencyReceiptDisposition.Completed,
            IdempotencyStatus.Failed => IdempotencyReceiptDisposition.Failed,
            _ => throw new InvalidOperationException($"Unsupported idempotency status: {Status}."),
        };
    }
}
