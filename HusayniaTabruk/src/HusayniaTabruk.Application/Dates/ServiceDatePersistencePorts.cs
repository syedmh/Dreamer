using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public partial interface IServiceDateRepository
{
    ValueTask<Result> CreateAsync(
        Domain.Dates.ServiceDate serviceDate,
        DatePersistenceEffects effects,
        CancellationToken cancellationToken = default);

    ValueTask<Result<LoadedServiceDate>> GetAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<LoadedServiceDate>> GetByHelpNeedAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This service-date repository does not support help-need hydration.");

    ValueTask<Result> RevalidateManagedAuthorityAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        MembershipId actorMembershipId,
        CancellationToken cancellationToken = default);

    ValueTask<Result> SaveAsync(
        LoadedServiceDate loaded,
        DatePersistenceEffects effects,
        CancellationToken cancellationToken = default);

    ValueTask<Result> SaveNeedAsync(
        LoadedServiceDate loaded,
        HelpNeedId helpNeedId,
        long expectedHelpNeedVersion,
        long? expectedSignupVersion,
        DatePersistenceEffects effects,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This service-date repository does not support help-need persistence.");

    ValueTask<Result<OpenServiceDatesPage>> ListOpenAsync(
        OrganizationId organizationId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default);
}
