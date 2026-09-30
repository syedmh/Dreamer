using HusayniaTabruk.Application.Abstractions.Time;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
