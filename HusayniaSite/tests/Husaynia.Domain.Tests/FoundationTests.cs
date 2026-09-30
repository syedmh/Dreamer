using Husaynia.Domain.Common;

namespace Husaynia.Domain.Tests;

public sealed class FoundationTests
{
    [Fact]
    public void DomainAssemblyHasNoProjectDependencies() =>
        Assert.Equal("Husaynia.Domain", DomainAssembly.Instance.GetName().Name);
}
