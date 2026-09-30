using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Signups.Reassignment;

public sealed record ReassignWaitlistedSignupCommand(
    HelpNeedId HelpNeedId,
    SignupId SignupId,
    string? Reason,
    IdempotencyKey IdempotencyKey);
