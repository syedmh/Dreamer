using HusayniaTabruk.Application.Threads;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed partial class PostgresAuthenticationMembershipRepository
{
    public ValueTask<Result<IReadOnlyDictionary<MembershipId, string>>> GetThreadSenderDisplaysAsync(
        OrganizationId organizationId,
        IReadOnlyCollection<MembershipId> membershipIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(membershipIds);

        return PostgresDependencyFailure.ExecuteAsync(
            () => GetThreadSenderDisplaysCoreAsync(
                organizationId,
                membershipIds,
                cancellationToken));
    }

    private async ValueTask<Result<IReadOnlyDictionary<MembershipId, string>>>
        GetThreadSenderDisplaysCoreAsync(
            OrganizationId organizationId,
            IReadOnlyCollection<MembershipId> membershipIds,
            CancellationToken cancellationToken)
    {
        Guid organizationValue = organizationId.EnsureValid().Value;
        Guid[] requestedIds = membershipIds
            .Select(membershipId => membershipId.EnsureValid().Value)
            .Distinct()
            .ToArray();
        if (requestedIds.Length == 0)
        {
            return Result.Success<IReadOnlyDictionary<MembershipId, string>>(
                new Dictionary<MembershipId, string>());
        }

        var rows = await context.Memberships
            .AsNoTracking()
            .Where(
                membership => membership.OrganizationId == organizationValue
                    && requestedIds.Contains(membership.Id))
            .Select(
                membership => new
                {
                    membership.Id,
                    membership.DisplayName,
                })
            .ToArrayAsync(cancellationToken);

        IReadOnlyDictionary<MembershipId, string> displays = rows.ToDictionary(
            row => MembershipId.From(row.Id),
            row => row.DisplayName);
        if (displays.Count != requestedIds.Length
            || displays.Values.Any(string.IsNullOrWhiteSpace))
        {
            return Result.Failure<IReadOnlyDictionary<MembershipId, string>>(
                DomainError.DependencyUnavailable(
                    ThreadApplicationErrorCodes.ThreadSenderDisplayUnavailable,
                    "Thread sender display names are temporarily unavailable."));
        }

        return Result.Success(displays);
    }
}
