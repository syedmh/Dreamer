using HusayniaTabruk.Application.Abstractions.Security;

namespace HusayniaTabruk.Application.Threads;

public static class ThreadStepUpPurposes
{
    public static StepUpPurpose PrivilegedRead { get; } = new("thread.privileged.read");
}
