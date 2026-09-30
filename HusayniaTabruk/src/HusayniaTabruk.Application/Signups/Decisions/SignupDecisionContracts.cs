using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Signups.Decisions;

public sealed record ApproveSignupCommand(
    SignupId SignupId,
    string? Reason,
    IdempotencyKey IdempotencyKey);

public sealed record DeclineSignupCommand(
    SignupId SignupId,
    string Reason,
    IdempotencyKey IdempotencyKey);

public sealed record WaitlistSignupCommand(
    SignupId SignupId,
    string? Reason,
    IdempotencyKey IdempotencyKey);
