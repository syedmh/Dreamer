using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Abstractions.Security;

public interface IStepUpVerifier
{
    ValueTask<Result<StepUpGrant>> IssueAsync(
        UserId userId,
        string credential,
        StepUpPurpose purpose,
        CancellationToken cancellationToken = default);

    ValueTask<Result> ConsumeAsync(
        UserId userId,
        StepUpToken token,
        StepUpPurpose purpose,
        CancellationToken cancellationToken = default);
}

public readonly record struct StepUpPurpose
{
    public StepUpPurpose(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct StepUpToken
{
    public StepUpToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record StepUpGrant(
    StepUpToken Token,
    StepUpPurpose Purpose,
    DateTimeOffset ExpiresAt);
