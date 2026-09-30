using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Tests.Architecture;

public sealed class SharedPrimitiveTests
{
    [Fact]
    public void StronglyTypedIdsRejectEmptyValuesAndRoundTripCanonicalUuidStrings()
    {
        Assert.Throws<ArgumentException>(() => OrganizationId.From(Guid.Empty));

        Guid value = Guid.Parse("5223a4ae-a182-481e-a39d-6b6d72b7d114");
        OrganizationId id = OrganizationId.From(value);

        Assert.Equal(value, id.Value);
        Assert.Equal("5223a4ae-a182-481e-a39d-6b6d72b7d114", id.ToString());
        Assert.Equal(id, OrganizationId.Parse(id.ToString(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(OrganizationId.TryParse(id.ToString(), out OrganizationId parsed));
        Assert.Equal(id, parsed);
        Assert.False(OrganizationId.TryParse("not-a-uuid", out _));
        Assert.False(default(OrganizationId).IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = default(OrganizationId).Value);
        Assert.Throws<InvalidOperationException>(() => default(OrganizationId).EnsureValid());
        Assert.Throws<InvalidOperationException>(() => default(OrganizationId).ToString());
        Assert.Equal(7, OrganizationId.New().Value.Version);
    }

    [Fact]
    public void DifferentIdTypesAreNotAssignable()
    {
        Assert.False(typeof(OrganizationId).IsAssignableFrom(typeof(MembershipId)));
        Assert.False(typeof(MembershipId).IsAssignableFrom(typeof(OrganizationId)));
    }

    [Fact]
    public void FrozenDomainEnumsHaveThePlannedNames()
    {
        Assert.Equal(["Invited", "Active", "Disabled"], Enum.GetNames<MembershipStatus>());
        Assert.Equal(["Admin", "FoodIncharge"], Enum.GetNames<OrganizationRole>());
        Assert.Equal(["PrivilegedThreadRead"], Enum.GetNames<PrivilegedPermission>());
        Assert.Equal(["Draft", "Open", "Closed", "Cancelled", "Completed"], Enum.GetNames<ServiceDateStatus>());
        Assert.Equal(["FoodPreparation", "Serving", "Cleanup"], Enum.GetNames<HelpCategory>());
        Assert.Equal(["Open", "Closed"], Enum.GetNames<HelpNeedStatus>());
        Assert.Equal(["Individual", "Household", "Team"], Enum.GetNames<SignupKind>());
        Assert.Equal(["Pending", "Approved", "Waitlisted", "Declined", "Withdrawn", "Cancelled"], Enum.GetNames<SignupStatus>());
        Assert.Equal(["Open", "Locked"], Enum.GetNames<ThreadStatus>());
        Assert.Equal(["Visible", "Hidden"], Enum.GetNames<MessageVisibility>());
    }

    [Fact]
    public void ResultSuccessAndFailureEnforceExclusiveStates()
    {
        Result<int> success = Result.Success(42);
        DomainError error = DomainError.Conflict(ErrorCodes.SignupDuplicate, "A signup already exists.");
        Result<int> failure = Result.Failure<int>(error);

        Assert.True(success.IsSuccess);
        Assert.Equal(42, success.Value);
        Assert.Throws<InvalidOperationException>(() => _ = success.Error);

        Assert.True(failure.IsFailure);
        Assert.Equal(error, failure.Error);
        Assert.Throws<InvalidOperationException>(() => _ = failure.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void DomainErrorsRequireStableCodesAndMessages(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new DomainError(invalidValue, "message", ErrorType.Conflict));
        Assert.Throws<ArgumentException>(() => new DomainError("code", invalidValue, ErrorType.Conflict));
    }

    [Fact]
    public void SharedLimitsMatchTheFrozenSecurityBounds()
    {
        Assert.Equal(50, ApplicationLimits.DefaultPageSize);
        Assert.Equal(100, ApplicationLimits.MaximumPageSize);
        Assert.Equal(2_000, ApplicationLimits.MaximumMessageUnicodeScalars);
        Assert.Equal(8 * 1024, ApplicationLimits.MaximumMessageUtf8Bytes);
        Assert.Equal(12 * 1024, ApplicationLimits.MaximumMessageRequestBytes);
        Assert.Equal(500, ApplicationLimits.MaximumReportCommentUnicodeScalars);
        Assert.Equal(2 * 1024, ApplicationLimits.MaximumReportCommentUtf8Bytes);
        Assert.Equal(4 * 1024, ApplicationLimits.MaximumReportRequestBytes);
        Assert.Equal(20, ApplicationLimits.MaximumNamedParticipants);
        Assert.Equal(20, ApplicationLimits.MaximumUnnamedParticipants);
        Assert.Equal(25, ApplicationLimits.MaximumTotalParticipants);
        Assert.Equal(80, ApplicationLimits.MaximumSignupLabelUnicodeScalars);
        Assert.Equal(320, ApplicationLimits.MaximumSignupLabelUtf8Bytes);
        Assert.Equal(8 * 1024, ApplicationLimits.MaximumSignupRequestBytes);
        Assert.Equal(500, ApplicationLimits.MaximumReasonUnicodeScalars);
        Assert.Equal(2 * 1024, ApplicationLimits.MaximumReasonUtf8Bytes);
        Assert.Equal(100, ApplicationLimits.MaximumCaseIdAsciiCharacters);
        Assert.Equal(4 * 1024, ApplicationLimits.MaximumAdministrativeRequestBytes);
        Assert.Equal(5, ApplicationLimits.StepUpLifetimeMinutes);
        Assert.Equal(3, ApplicationLimits.ThreadPostsPerTenSecondsPerAccount);
        Assert.Equal(10, ApplicationLimits.ThreadPostsPerMinutePerAccount);
        Assert.Equal(60, ApplicationLimits.ThreadPostsPerHourPerAccount);
        Assert.Equal(300, ApplicationLimits.ThreadPostsPerHourPerOrganization);
        Assert.Equal(5, ApplicationLimits.ReportsPerHourPerAccount);
        Assert.Equal(20, ApplicationLimits.ReportsPerDayPerAccount);
        Assert.Equal(100, ApplicationLimits.ReportsPerDayPerOrganization);
        Assert.Equal(10, ApplicationLimits.SignupSubmissionsPerMinutePerAccount);
        Assert.Equal(100, ApplicationLimits.SignupSubmissionsPerHourPerOrganization);
        Assert.Equal(60, ApplicationLimits.AdministrativeMutationsPerMinutePerAccount);
        Assert.Equal(500, ApplicationLimits.AdministrativeMutationsPerHourPerOrganization);
    }

    [Fact]
    public void StableErrorCodesMatchTheFrozenWireContract()
    {
        Assert.Equal("signup_duplicate", ErrorCodes.SignupDuplicate);
        Assert.Equal("category_closed", ErrorCodes.CategoryClosed);
        Assert.Equal("capacity_unavailable", ErrorCodes.CapacityUnavailable);
        Assert.Equal("cancellation_deadline_passed", ErrorCodes.CancellationDeadlinePassed);
        Assert.Equal("invalid_transition", ErrorCodes.InvalidTransition);
        Assert.Equal("stale_version", ErrorCodes.StaleVersion);
        Assert.Equal("payload_too_large", ErrorCodes.PayloadTooLarge);
        Assert.Equal("rate_limited", ErrorCodes.RateLimited);
    }
}
