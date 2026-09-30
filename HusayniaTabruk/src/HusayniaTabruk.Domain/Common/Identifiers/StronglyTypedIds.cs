using System.Diagnostics.CodeAnalysis;

namespace HusayniaTabruk.Domain.Common.Identifiers;

// C# permits default construction of record structs. The default value is intentionally invalid;
// conversion and persistence boundaries must call EnsureValid(), while From/Parse reject empty UUIDs.
public readonly record struct UserId
{
    private readonly Guid _value;

    private UserId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(UserId));
    public bool IsValid => _value != Guid.Empty;
    public static UserId New() => From(Guid.CreateVersion7());
    public static UserId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(UserId)));
    public static UserId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out UserId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out UserId result) => TryParse(value, null, out result);
    public UserId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(UserId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct OrganizationId
{
    private readonly Guid _value;

    private OrganizationId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(OrganizationId));
    public bool IsValid => _value != Guid.Empty;
    public static OrganizationId New() => From(Guid.CreateVersion7());
    public static OrganizationId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(OrganizationId)));
    public static OrganizationId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out OrganizationId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out OrganizationId result) => TryParse(value, null, out result);
    public OrganizationId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(OrganizationId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct MembershipId
{
    private readonly Guid _value;

    private MembershipId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(MembershipId));
    public bool IsValid => _value != Guid.Empty;
    public static MembershipId New() => From(Guid.CreateVersion7());
    public static MembershipId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(MembershipId)));
    public static MembershipId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out MembershipId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out MembershipId result) => TryParse(value, null, out result);
    public MembershipId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(MembershipId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct ServiceDateId
{
    private readonly Guid _value;

    private ServiceDateId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(ServiceDateId));
    public bool IsValid => _value != Guid.Empty;
    public static ServiceDateId New() => From(Guid.CreateVersion7());
    public static ServiceDateId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(ServiceDateId)));
    public static ServiceDateId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out ServiceDateId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out ServiceDateId result) => TryParse(value, null, out result);
    public ServiceDateId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(ServiceDateId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct HelpNeedId
{
    private readonly Guid _value;

    private HelpNeedId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(HelpNeedId));
    public bool IsValid => _value != Guid.Empty;
    public static HelpNeedId New() => From(Guid.CreateVersion7());
    public static HelpNeedId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(HelpNeedId)));
    public static HelpNeedId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out HelpNeedId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out HelpNeedId result) => TryParse(value, null, out result);
    public HelpNeedId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(HelpNeedId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct SignupId
{
    private readonly Guid _value;

    private SignupId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(SignupId));
    public bool IsValid => _value != Guid.Empty;
    public static SignupId New() => From(Guid.CreateVersion7());
    public static SignupId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(SignupId)));
    public static SignupId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out SignupId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out SignupId result) => TryParse(value, null, out result);
    public SignupId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(SignupId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct ThreadId
{
    private readonly Guid _value;

    private ThreadId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(ThreadId));
    public bool IsValid => _value != Guid.Empty;
    public static ThreadId New() => From(Guid.CreateVersion7());
    public static ThreadId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(ThreadId)));
    public static ThreadId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out ThreadId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out ThreadId result) => TryParse(value, null, out result);
    public ThreadId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(ThreadId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct MessageId
{
    private readonly Guid _value;

    private MessageId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(MessageId));
    public bool IsValid => _value != Guid.Empty;
    public static MessageId New() => From(Guid.CreateVersion7());
    public static MessageId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(MessageId)));
    public static MessageId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out MessageId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out MessageId result) => TryParse(value, null, out result);
    public MessageId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(MessageId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct NotificationId
{
    private readonly Guid _value;

    private NotificationId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(NotificationId));
    public bool IsValid => _value != Guid.Empty;
    public static NotificationId New() => From(Guid.CreateVersion7());
    public static NotificationId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(NotificationId)));
    public static NotificationId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out NotificationId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out NotificationId result) => TryParse(value, null, out result);
    public NotificationId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(NotificationId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct RoleChangeRequestId
{
    private readonly Guid _value;

    private RoleChangeRequestId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(RoleChangeRequestId));
    public bool IsValid => _value != Guid.Empty;
    public static RoleChangeRequestId New() => From(Guid.CreateVersion7());
    public static RoleChangeRequestId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(RoleChangeRequestId)));
    public static RoleChangeRequestId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out RoleChangeRequestId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out RoleChangeRequestId result) => TryParse(value, null, out result);
    public RoleChangeRequestId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(RoleChangeRequestId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct AuditEventId
{
    private readonly Guid _value;

    private AuditEventId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(AuditEventId));
    public bool IsValid => _value != Guid.Empty;
    public static AuditEventId New() => From(Guid.CreateVersion7());
    public static AuditEventId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(AuditEventId)));
    public static AuditEventId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out AuditEventId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out AuditEventId result) => TryParse(value, null, out result);
    public AuditEventId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(AuditEventId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct OutboxMessageId
{
    private readonly Guid _value;

    private OutboxMessageId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(OutboxMessageId));
    public bool IsValid => _value != Guid.Empty;
    public static OutboxMessageId New() => From(Guid.CreateVersion7());
    public static OutboxMessageId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(OutboxMessageId)));
    public static OutboxMessageId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out OutboxMessageId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out OutboxMessageId result) => TryParse(value, null, out result);
    public OutboxMessageId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(OutboxMessageId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct DeviceId
{
    private readonly Guid _value;

    private DeviceId(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(DeviceId));
    public bool IsValid => _value != Guid.Empty;
    public static DeviceId New() => From(Guid.CreateVersion7());
    public static DeviceId From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(DeviceId)));
    public static DeviceId Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out DeviceId result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out DeviceId result) => TryParse(value, null, out result);
    public DeviceId EnsureValid() => Identifier.EnsureValid(this, _value, nameof(DeviceId));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

public readonly record struct IdempotencyKey
{
    private readonly Guid _value;

    private IdempotencyKey(Guid value) => _value = value;

    public Guid Value => Identifier.EnsureAccessible(_value, nameof(IdempotencyKey));
    public bool IsValid => _value != Guid.Empty;
    public static IdempotencyKey New() => From(Guid.CreateVersion7());
    public static IdempotencyKey From(Guid value) => new(Identifier.EnsureNotEmpty(value, nameof(IdempotencyKey)));
    public static IdempotencyKey Parse(string value, IFormatProvider? provider = null) => From(Identifier.Parse(value));
    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, out IdempotencyKey result) =>
        Identifier.TryParse(value, From, out result);
    public static bool TryParse([NotNullWhen(true)] string? value, out IdempotencyKey result) => TryParse(value, null, out result);
    public IdempotencyKey EnsureValid() => Identifier.EnsureValid(this, _value, nameof(IdempotencyKey));
    public override string ToString()
    {
        EnsureValid();
        return _value.ToString("D");
    }
}

internal static class Identifier
{
    public static Guid EnsureNotEmpty(Guid value, string identifierName) =>
        value == Guid.Empty ? throw new ArgumentException($"{identifierName} cannot be empty.", nameof(value)) : value;

    public static Guid EnsureAccessible(Guid value, string identifierName) =>
        value == Guid.Empty
            ? throw CreateDefaultValueException(identifierName)
            : value;

    public static T EnsureValid<T>(T identifier, Guid value, string identifierName) =>
        value == Guid.Empty
            ? throw CreateDefaultValueException(identifierName)
            : identifier;

    public static Guid Parse(string value) =>
        Guid.TryParseExact(value, "D", out Guid parsed)
            ? parsed
            : throw new FormatException("Identifier must be a canonical UUID string.");

    public static bool TryParse<T>(string? value, Func<Guid, T> factory, out T result)
    {
        if (Guid.TryParseExact(value, "D", out Guid parsed) && parsed != Guid.Empty)
        {
            result = factory(parsed);
            return true;
        }

        result = default!;
        return false;
    }

    private static InvalidOperationException CreateDefaultValueException(string identifierName) =>
        new($"{identifierName} is the language-level default value and cannot cross a conversion or persistence boundary.");
}
