namespace HusayniaTabruk.Domain.Common.Errors;

public static class ErrorCodes
{
    public const string SignupDuplicate = "signup_duplicate";
    public const string CategoryClosed = "category_closed";
    public const string CapacityUnavailable = "capacity_unavailable";
    public const string CancellationDeadlinePassed = "cancellation_deadline_passed";
    public const string InvalidTransition = "invalid_transition";
    public const string StaleVersion = "stale_version";
    public const string PayloadTooLarge = "payload_too_large";
    public const string RateLimited = "rate_limited";
    public const string DependencyUnavailable = "dependency_unavailable";
}
