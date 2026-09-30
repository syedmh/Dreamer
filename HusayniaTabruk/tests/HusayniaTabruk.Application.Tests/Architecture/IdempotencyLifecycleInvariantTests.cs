using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Tests.Architecture;

public sealed class IdempotencyLifecycleInvariantTests
{
    public static TheoryData<IdempotencyStatus, string?> InvalidReceiptStates =>
        new()
        {
            { IdempotencyStatus.Processing, "signup-123" },
            { IdempotencyStatus.Processing, string.Empty },
            { IdempotencyStatus.Processing, " " },
            { IdempotencyStatus.Completed, null },
            { IdempotencyStatus.Completed, string.Empty },
            { IdempotencyStatus.Completed, " " },
            { IdempotencyStatus.Failed, "signup-123" },
            { IdempotencyStatus.Failed, string.Empty },
            { IdempotencyStatus.Failed, " " },
        };

    public static TheoryData<IdempotencyCreateOutcome, IdempotencyStatus> InvalidCreateResultStates =>
        new()
        {
            { IdempotencyCreateOutcome.Created, IdempotencyStatus.Completed },
            { IdempotencyCreateOutcome.Created, IdempotencyStatus.Failed },
            { IdempotencyCreateOutcome.ExistingProcessing, IdempotencyStatus.Completed },
            { IdempotencyCreateOutcome.ExistingProcessing, IdempotencyStatus.Failed },
            { IdempotencyCreateOutcome.ExistingCompleted, IdempotencyStatus.Processing },
            { IdempotencyCreateOutcome.ExistingCompleted, IdempotencyStatus.Failed },
            { IdempotencyCreateOutcome.ExistingFailed, IdempotencyStatus.Processing },
            { IdempotencyCreateOutcome.ExistingFailed, IdempotencyStatus.Completed },
        };

    public static TheoryData<IdempotencyTransitionOutcome, IdempotencyStatus> InvalidTransitionResultStates =>
        new()
        {
            { IdempotencyTransitionOutcome.Completed, IdempotencyStatus.Processing },
            { IdempotencyTransitionOutcome.Completed, IdempotencyStatus.Failed },
            { IdempotencyTransitionOutcome.Failed, IdempotencyStatus.Processing },
            { IdempotencyTransitionOutcome.Failed, IdempotencyStatus.Completed },
            { IdempotencyTransitionOutcome.Missing, IdempotencyStatus.Processing },
            { IdempotencyTransitionOutcome.Missing, IdempotencyStatus.Completed },
            { IdempotencyTransitionOutcome.Missing, IdempotencyStatus.Failed },
            { IdempotencyTransitionOutcome.ExpectedStatusMismatch, IdempotencyStatus.Processing },
        };

    [Theory]
    [MemberData(nameof(InvalidReceiptStates))]
    public void ReceiptRejectsStatusAndResultReferenceContradictions(
        IdempotencyStatus status,
        string? resultReference)
    {
        Assert.Throws<ArgumentException>(() => CreateReceipt(status, resultReference));
    }

    [Fact]
    public void ReceiptAcceptsEachValidStatusAndResultReferenceBoundary()
    {
        Assert.Null(CreateReceipt(IdempotencyStatus.Processing, null).ResultReference);
        Assert.Equal("signup-123", CreateReceipt(IdempotencyStatus.Completed, "signup-123").ResultReference);
        Assert.Null(CreateReceipt(IdempotencyStatus.Failed, null).ResultReference);
    }

    [Theory]
    [MemberData(nameof(InvalidCreateResultStates))]
    public void CreateResultRejectsOutcomeAndReceiptStatusContradictions(
        IdempotencyCreateOutcome outcome,
        IdempotencyStatus status)
    {
        IdempotencyReceipt receipt = CreateReceiptForStatus(status);

        Assert.Throws<ArgumentException>(() => new IdempotencyCreateResult(outcome, receipt));
    }

    [Theory]
    [InlineData(IdempotencyCreateOutcome.Created)]
    [InlineData(IdempotencyCreateOutcome.ExistingProcessing)]
    [InlineData(IdempotencyCreateOutcome.ExistingCompleted)]
    [InlineData(IdempotencyCreateOutcome.ExistingFailed)]
    [InlineData(IdempotencyCreateOutcome.RequestMismatch)]
    [InlineData(IdempotencyCreateOutcome.Expired)]
    public void CreateResultRejectsMissingReceipt(IdempotencyCreateOutcome outcome)
    {
        Assert.Throws<ArgumentNullException>(() => new IdempotencyCreateResult(outcome, null!));
    }

    [Theory]
    [InlineData(IdempotencyCreateOutcome.Created, IdempotencyStatus.Processing)]
    [InlineData(IdempotencyCreateOutcome.ExistingProcessing, IdempotencyStatus.Processing)]
    [InlineData(IdempotencyCreateOutcome.ExistingCompleted, IdempotencyStatus.Completed)]
    [InlineData(IdempotencyCreateOutcome.ExistingFailed, IdempotencyStatus.Failed)]
    [InlineData(IdempotencyCreateOutcome.RequestMismatch, IdempotencyStatus.Processing)]
    [InlineData(IdempotencyCreateOutcome.RequestMismatch, IdempotencyStatus.Completed)]
    [InlineData(IdempotencyCreateOutcome.RequestMismatch, IdempotencyStatus.Failed)]
    [InlineData(IdempotencyCreateOutcome.Expired, IdempotencyStatus.Processing)]
    [InlineData(IdempotencyCreateOutcome.Expired, IdempotencyStatus.Completed)]
    [InlineData(IdempotencyCreateOutcome.Expired, IdempotencyStatus.Failed)]
    public void CreateResultAcceptsEachValidOutcomeAndReceiptStatusBoundary(
        IdempotencyCreateOutcome outcome,
        IdempotencyStatus status)
    {
        IdempotencyReceipt receipt = CreateReceiptForStatus(status);

        IdempotencyCreateResult result = new(outcome, receipt);

        Assert.Equal(status, result.Receipt.Status);
    }

    [Theory]
    [MemberData(nameof(InvalidTransitionResultStates))]
    public void TransitionResultRejectsOutcomeAndReceiptStatusContradictions(
        IdempotencyTransitionOutcome outcome,
        IdempotencyStatus status)
    {
        IdempotencyReceipt receipt = CreateReceiptForStatus(status);

        Assert.Throws<ArgumentException>(() => new IdempotencyTransitionResult(outcome, receipt));
    }

    [Theory]
    [InlineData(IdempotencyTransitionOutcome.Completed)]
    [InlineData(IdempotencyTransitionOutcome.Failed)]
    [InlineData(IdempotencyTransitionOutcome.RequestMismatch)]
    [InlineData(IdempotencyTransitionOutcome.ExpectedStatusMismatch)]
    public void NonMissingTransitionResultRejectsMissingReceipt(IdempotencyTransitionOutcome outcome)
    {
        Assert.Throws<ArgumentNullException>(() => new IdempotencyTransitionResult(outcome, null));
    }

    [Theory]
    [InlineData(IdempotencyTransitionOutcome.Completed, IdempotencyStatus.Completed)]
    [InlineData(IdempotencyTransitionOutcome.Failed, IdempotencyStatus.Failed)]
    [InlineData(IdempotencyTransitionOutcome.RequestMismatch, IdempotencyStatus.Processing)]
    [InlineData(IdempotencyTransitionOutcome.RequestMismatch, IdempotencyStatus.Completed)]
    [InlineData(IdempotencyTransitionOutcome.RequestMismatch, IdempotencyStatus.Failed)]
    [InlineData(IdempotencyTransitionOutcome.ExpectedStatusMismatch, IdempotencyStatus.Completed)]
    [InlineData(IdempotencyTransitionOutcome.ExpectedStatusMismatch, IdempotencyStatus.Failed)]
    public void TransitionResultAcceptsEachValidOutcomeAndReceiptStatusBoundary(
        IdempotencyTransitionOutcome outcome,
        IdempotencyStatus status)
    {
        IdempotencyReceipt receipt = CreateReceiptForStatus(status);

        IdempotencyTransitionResult result = new(outcome, receipt);

        Assert.Equal(status, result.Receipt!.Status);
    }

    [Fact]
    public void MissingTransitionAcceptsOnlyNullReceipt()
    {
        IdempotencyTransitionResult result = new(IdempotencyTransitionOutcome.Missing, null);

        Assert.Null(result.Receipt);
    }

    private static IdempotencyReceipt CreateReceiptForStatus(IdempotencyStatus status) =>
        CreateReceipt(
            status,
            status == IdempotencyStatus.Completed ? "signup-123" : null);

    private static IdempotencyReceipt CreateReceipt(
        IdempotencyStatus status,
        string? resultReference)
    {
        DateTimeOffset createdAt = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        return new(
            OrganizationId.New(),
            MembershipId.New(),
            IdempotencyKey.New(),
            "signup.submit",
            RequestFingerprint.FromSha256(new string('a', 64)),
            status,
            resultReference,
            createdAt,
            createdAt.AddMinutes(10));
    }
}
