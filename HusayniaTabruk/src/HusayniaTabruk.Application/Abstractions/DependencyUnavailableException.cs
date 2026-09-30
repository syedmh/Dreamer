namespace HusayniaTabruk.Application.Abstractions;

public sealed class DependencyUnavailableException : Exception
{
    public DependencyUnavailableException(Exception? innerException = null)
        : base("A required dependency is temporarily unavailable.", innerException)
    {
    }
}
