namespace Husaynia.Application.Contracts;

public interface IEventReader
{
    Task<Result<EventPage, CalendarError>> GetBySlugAsync(string slug, CancellationToken ct);
}

public interface ICalendarExporter
{
    Task<Result<IcalDocument, CalendarError>> ExportAsync(EventSelection selection, CancellationToken ct);
}

public interface IPrayerScheduleService
{
    Task<Result<PrayerSchedule, PrayerError>> GetMonthAsync(
        YearMonth month,
        string timeZoneId,
        CancellationToken ct);
}

public interface IPublicSearch
{
    Task<Result<SearchPage, SearchError>> SearchAsync(
        string query,
        int page,
        int pageSize,
        CancellationToken ct);
}

public interface IFormSubmissionService
{
    Task<Result<FormReceipt, FormError>> SubmitAsync(
        FormSubmissionCommand command,
        RequestFingerprint fingerprint,
        CancellationToken ct);
}

public interface IDonationService
{
    Task<Result<CheckoutRedirect, DonationError>> CreateCheckoutAsync(
        DonationIntent intent,
        string idempotencyKey,
        CancellationToken ct);

    Task<Result<WebhookReceipt, WebhookError>> HandleWebhookAsync(
        ReadOnlyMemory<byte> body,
        string signature,
        CancellationToken ct);
}

public interface IImportPlanner
{
    Task<Result<ImportPlan, ImportError>> PlanAsync(
        ImportSource source,
        ImportMode mode,
        CancellationToken ct);
}

public interface IImportExecutor
{
    Task<Result<ImportReceipt, ImportError>> ApplyAsync(
        ImportPlanId planId,
        UserContext actor,
        CancellationToken ct);
}

public sealed record EventPage(
    string Slug,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TimeZoneId,
    string CanonicalPath);

public sealed record EventSelection(IReadOnlyList<string> Slugs);

public sealed record IcalDocument(string Content, string ContentType = "text/calendar; charset=utf-8");

public sealed record PrayerTime(string Name, TimeOnly LocalTime);

public sealed record PrayerDay(DateOnly Date, IReadOnlyList<PrayerTime> Times, bool IsOverride);

public sealed record PrayerSchedule(
    YearMonth Month,
    string TimeZoneId,
    IReadOnlyList<PrayerDay> Days,
    DateTimeOffset GeneratedAtUtc,
    bool IsStale);

public sealed record SearchResult(string Title, string CanonicalPath, string Summary);

public sealed record SearchPage(
    IReadOnlyList<SearchResult> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record FormSubmissionCommand(
    string FormKey,
    IReadOnlyDictionary<string, string> Fields,
    string? ConsentVersion);

public sealed record FormReceipt(Guid SubmissionId, DateTimeOffset AcceptedAtUtc);

public sealed record DonationIntent(
    string Category,
    decimal Amount,
    string Currency,
    string Mode,
    bool IsAnonymous);

public sealed record CheckoutRedirect(Uri Location, string CheckoutReference);

public sealed record WebhookReceipt(string ProviderEventId, bool WasAlreadyProcessed);

public enum ImportMode
{
    DryRun = 0,
    Apply = 1,
}

public sealed record ImportSource(string Kind, string Version, Uri Location);

public sealed record ImportPlan(ImportPlanId Id, ImportMode Mode, int CandidateCount);

public sealed record ImportReceipt(ImportPlanId PlanId, int AppliedCount, int ConflictCount);
