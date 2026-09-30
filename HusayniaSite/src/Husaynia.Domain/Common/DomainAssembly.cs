namespace Husaynia.Domain.Common;

/// <summary>
/// Provides a stable marker for locating the Domain assembly without coupling to a feature type.
/// </summary>
public static class DomainAssembly
{
    public static System.Reflection.Assembly Instance => typeof(DomainAssembly).Assembly;
}
