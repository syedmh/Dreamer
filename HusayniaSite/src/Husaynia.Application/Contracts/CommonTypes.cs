namespace Husaynia.Application.Contracts;

public readonly record struct ContentId(Guid Value);

public readonly record struct RevisionId(Guid Value);

public readonly record struct ImportPlanId(Guid Value);

public readonly record struct PreviewToken(string Value);

public readonly record struct RequestFingerprint(string Value);

public readonly record struct JobKey(string Value);

public readonly record struct WorkerIdentity(string Value);

public readonly record struct RowVersion(ReadOnlyMemory<byte> Value);

public readonly record struct YearMonth
{
    public YearMonth(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        Year = year;
        Month = month;
    }

    public int Year { get; }

    public int Month { get; }
}

public sealed record UserContext(string UserId, IReadOnlySet<string> Roles, string CorrelationId);

public sealed record ContractError(string Code, string Message);

public sealed record ContentError(string Code, string Message);

public sealed record PublishError(string Code, string Message);

public sealed record CalendarError(string Code, string Message);

public sealed record PrayerError(string Code, string Message);

public sealed record SearchError(string Code, string Message);

public sealed record FormError(string Code, string Message);

public sealed record DonationError(string Code, string Message);

public sealed record WebhookError(string Code, string Message);

public sealed record ImportError(string Code, string Message);

public sealed record IntegrationError(string Code, string Message);
