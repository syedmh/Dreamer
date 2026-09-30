using System.Globalization;
using System.Text;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Application.Signups.Participants;

public sealed class EligibleParticipantQueryService(
    IEligibleSignupParticipantRepository participantRepository,
    IMembershipRepository membershipRepository,
    ICurrentActor currentActor)
{
    public async ValueTask<Result<EligibleSignupParticipantPage>> ListAsync(
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        Result<ActiveMembershipContext> actor =
            await membershipRepository.ResolveActiveActorAsync(
                currentActor.UserId,
                currentActor.MembershipId,
                currentActor.OrganizationId,
                cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<EligibleSignupParticipantPage>(actor.Error);
        }

        Result<(int StartIndex, int PageSize)> page = ValidatePage(cursor, pageSize);
        if (page.IsFailure)
        {
            return Result.Failure<EligibleSignupParticipantPage>(page.Error);
        }

        Result<EligibleSignupParticipantSlice> participants =
            await participantRepository.ListEligibleAsync(
                actor.Value.OrganizationId,
                actor.Value.MembershipId,
                page.Value.StartIndex,
                page.Value.PageSize,
                cancellationToken);
        if (participants.IsFailure)
        {
            return Result.Failure<EligibleSignupParticipantPage>(participants.Error);
        }

        string? nextCursor = participants.Value.HasMore
            ? EncodeCursor(page.Value.StartIndex + participants.Value.Items.Count)
            : null;
        return Result.Success(
            new EligibleSignupParticipantPage(
                participants.Value.Items,
                nextCursor));
    }

    private static Result<(int StartIndex, int PageSize)> ValidatePage(
        string? cursor,
        int? requestedPageSize)
    {
        if (requestedPageSize is <= 0 or > ApplicationLimits.MaximumPageSize)
        {
            return Result.Failure<(int, int)>(
                EligibleParticipantErrorCodes.Validation(
                    $"Page size must be from 1 through {ApplicationLimits.MaximumPageSize}."));
        }

        Result<int> startIndex = DecodeCursor(cursor);
        if (startIndex.IsFailure)
        {
            return Result.Failure<(int, int)>(startIndex.Error);
        }

        int pageSize = requestedPageSize ?? ApplicationLimits.DefaultPageSize;
        return Result.Success((startIndex.Value, pageSize));
    }

    private static Result<int> DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return Result.Success(0);
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(cursor));
            return int.TryParse(
                    decoded,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int value)
                && value >= 0
                ? Result.Success(value)
                : Result.Failure<int>(
                    EligibleParticipantErrorCodes.Validation(
                        "The cursor is invalid."));
        }
        catch (FormatException)
        {
            return Result.Failure<int>(
                EligibleParticipantErrorCodes.Validation(
                    "The cursor is invalid."));
        }
    }

    private static string EncodeCursor(int value) =>
        Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                value.ToString(CultureInfo.InvariantCulture)));
}
