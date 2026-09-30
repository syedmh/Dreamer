using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Signups.Cancellation;

public sealed record WithdrawSignupCommand(
    SignupId SignupId,
    IdempotencyKey IdempotencyKey);

public sealed record OverrideSignupCancellationCommand(
    SignupId SignupId,
    SignupStatus TargetStatus,
    string Reason,
    IdempotencyKey IdempotencyKey);
