namespace HusayniaTabruk.Domain.Common;

public static class ApplicationLimits
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;

    public const int MaximumMessageUnicodeScalars = 2_000;
    public const int MaximumMessageUtf8Bytes = 8 * 1024;
    public const int MaximumMessageRequestBytes = 12 * 1024;

    public const int MaximumReportCommentUnicodeScalars = 500;
    public const int MaximumReportCommentUtf8Bytes = 2 * 1024;
    public const int MaximumReportRequestBytes = 4 * 1024;

    public const int MaximumNamedParticipants = 20;
    public const int MaximumUnnamedParticipants = 20;
    public const int MaximumTotalParticipants = 25;
    public const int MaximumSignupLabelUnicodeScalars = 80;
    public const int MaximumSignupLabelUtf8Bytes = 320;
    public const int MaximumSignupRequestBytes = 8 * 1024;

    public const int MaximumReasonUnicodeScalars = 500;
    public const int MaximumReasonUtf8Bytes = 2 * 1024;
    public const int MaximumCaseIdAsciiCharacters = 100;
    public const int MaximumAdministrativeRequestBytes = 4 * 1024;

    public const int StepUpLifetimeMinutes = 5;

    public const int ThreadPostsPerTenSecondsPerAccount = 3;
    public const int ThreadPostsPerMinutePerAccount = 10;
    public const int ThreadPostsPerHourPerAccount = 60;
    public const int ThreadPostsPerHourPerOrganization = 300;

    public const int ReportsPerHourPerAccount = 5;
    public const int ReportsPerDayPerAccount = 20;
    public const int ReportsPerDayPerOrganization = 100;

    public const int SignupSubmissionsPerMinutePerAccount = 10;
    public const int SignupSubmissionsPerHourPerOrganization = 100;

    public const int AdministrativeMutationsPerMinutePerAccount = 60;
    public const int AdministrativeMutationsPerHourPerOrganization = 500;
}
