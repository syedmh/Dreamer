using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Signups.Submit;

public sealed record SubmitSignupCommand(
    HelpNeedId HelpNeedId,
    SignupKind Kind,
    string? Label,
    IReadOnlyCollection<MembershipId> MemberParticipantIds,
    int UnnamedParticipantCount,
    IdempotencyKey IdempotencyKey);
