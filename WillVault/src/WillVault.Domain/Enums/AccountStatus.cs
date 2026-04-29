namespace WillVault.Domain.Enums;

public enum AccountStatus
{
    Active = 0,
    VerificationPending = 1,
    Verified = 2,
    ReleaseScheduled = 3,
    Releasing = 4,
    Closed = 5
}
