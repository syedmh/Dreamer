using HusayniaTabruk.Application.Signups.Participants;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed partial class PostgresAuthenticationMembershipRepository
    : IEligibleSignupParticipantRepository
{
    public ValueTask<Result<EligibleSignupParticipantSlice>> ListEligibleAsync(
        OrganizationId organizationId,
        MembershipId excludedMembershipId,
        int startIndex,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => ListEligibleCoreAsync(
                organizationId,
                excludedMembershipId,
                startIndex,
                pageSize,
                cancellationToken));

    private async ValueTask<Result<EligibleSignupParticipantSlice>> ListEligibleCoreAsync(
        OrganizationId organizationId,
        MembershipId excludedMembershipId,
        int startIndex,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        if (pageSize is < 1 or > ApplicationLimits.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"Page size must be from 1 through {ApplicationLimits.MaximumPageSize}.");
        }

        var rows = await context.Memberships
            .AsNoTracking()
            .Where(
                candidate => candidate.OrganizationId == organizationId.Value
                    && candidate.Id != excludedMembershipId.Value
                    && candidate.Status == (short)MembershipStatus.Active
                    && candidate.EligibleAsNamedParticipant)
            .OrderBy(candidate => candidate.DisplayName)
            .ThenBy(candidate => candidate.Id)
            .Skip(startIndex)
            .Take(pageSize + 1)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.DisplayName,
            })
            .ToListAsync(cancellationToken);

        bool hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        EligibleSignupParticipantSummary[] items = rows
            .Select(
                candidate => new EligibleSignupParticipantSummary(
                    MembershipId.From(candidate.Id),
                    candidate.DisplayName))
            .ToArray();
        return Result.Success(
            new EligibleSignupParticipantSlice(items, hasMore));
    }
}
