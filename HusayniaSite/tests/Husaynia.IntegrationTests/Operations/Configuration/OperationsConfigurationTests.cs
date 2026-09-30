using Husaynia.Infrastructure.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Husaynia.IntegrationTests.Operations.Configuration;

public sealed class OperationsConfigurationTests
{
    [Fact]
    public void DefaultsAreValidAndUnsafeLeaseSettingsAreRejected()
    {
        var validator = new OperationsConfigurationValidator();
        var defaults = new TestConfiguration(
            new Dictionary<string, string?>());
        var invalid = new TestConfiguration(
            new Dictionary<string, string?>
            {
                ["Operations:Jobs:LeaseDuration"] = "00:00:30",
                ["Operations:Jobs:LeaseRenewalInterval"] = "00:00:30",
                ["Operations:Jobs:HandlerTimeout"] = "02:00:00",
                ["Operations:Jobs:PollingInterval"] = "00:10:00",
                ["Operations:Jobs:JitterRatio"] = "1.5",
            });

        Assert.Empty(validator.Validate(defaults));
        var failures = validator.Validate(invalid);
        Assert.Contains(failures, failure => failure.Contains("LeaseRenewalInterval", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("HandlerTimeout", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("PollingInterval", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("JitterRatio", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedDurationIsReportedWithoutThrowing()
    {
        var configuration = new TestConfiguration(
            new Dictionary<string, string?>
            {
                ["Operations:Jobs:LeaseDuration"] = "forever",
            });

        var failures = new OperationsConfigurationValidator().Validate(configuration);

        Assert.Contains(failures, failure => failure.Contains("must be a TimeSpan", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("LeaseDuration", "00:00:00")]
    [InlineData("LeaseDuration", "01:00:00.001")]
    [InlineData("LeaseRenewalInterval", "00:00:00")]
    [InlineData("RecoveryDelay", "-00:00:00.001")]
    [InlineData("RecoveryDelay", "00:10:00.001")]
    [InlineData("HandlerTimeout", "00:00:00")]
    [InlineData("HandlerTimeout", "01:00:00.001")]
    [InlineData("PollingInterval", "00:00:00")]
    [InlineData("PollingInterval", "00:05:00.001")]
    public void EveryDurationTrustBoundaryRejectsUnsafeValues(string key, string value)
    {
        var configuration = new TestConfiguration(
            new Dictionary<string, string?>
            {
                [$"Operations:Jobs:{key}"] = value,
            });

        var failures = new OperationsConfigurationValidator().Validate(configuration);

        Assert.Contains(failures, failure => failure.Contains(key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void JitterTrustBoundaryRejectsNonFiniteAndOutOfRangeValues(string value)
    {
        var configuration = new TestConfiguration(
            new Dictionary<string, string?>
            {
                ["Operations:Jobs:JitterRatio"] = value,
            });

        var failures = new OperationsConfigurationValidator().Validate(configuration);

        Assert.Contains(
            failures,
            failure => failure.Contains("JitterRatio", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedJitterIsReportedWithoutFallingSilentlyToDefault()
    {
        var configuration = new TestConfiguration(
            new Dictionary<string, string?>
            {
                ["Operations:Jobs:JitterRatio"] = "not-a-number",
            });

        var failures = new OperationsConfigurationValidator().Validate(configuration);

        Assert.Contains(
            failures,
            failure => failure.Contains("must be a number", StringComparison.Ordinal));
    }

    [Fact]
    public void LeaseRenewalMustRemainStrictlyShorterThanLease()
    {
        var configuration = new TestConfiguration(
            new Dictionary<string, string?>
            {
                ["Operations:Jobs:LeaseDuration"] = "00:00:30",
                ["Operations:Jobs:LeaseRenewalInterval"] = "00:00:30",
            });

        var failures = new OperationsConfigurationValidator().Validate(configuration);

        Assert.Contains(
            failures,
            failure => failure.Contains("shorter than the lease", StringComparison.Ordinal));
    }

    private sealed class TestConfiguration(
        IReadOnlyDictionary<string, string?> values) : IConfiguration
    {
        public string? this[string key]
        {
            get => values.GetValueOrDefault(key);
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => NeverChangeToken.Instance;

        public IConfigurationSection GetSection(string key) =>
            new TestConfigurationSection(this, key);
    }

    private sealed class TestConfigurationSection(
        IConfiguration root,
        string path) : IConfigurationSection
    {
        public string Key => path.Split(':')[^1];

        public string Path => path;

        public string? Value
        {
            get => root[path];
            set => throw new NotSupportedException();
        }

        public string? this[string key]
        {
            get => root[$"{path}:{key}"];
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => NeverChangeToken.Instance;

        public IConfigurationSection GetSection(string key) =>
            new TestConfigurationSection(root, $"{path}:{key}");
    }

    private sealed class NeverChangeToken : IChangeToken
    {
        internal static NeverChangeToken Instance { get; } = new();

        public bool HasChanged => false;

        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
            EmptyDisposable.Instance;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        internal static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
