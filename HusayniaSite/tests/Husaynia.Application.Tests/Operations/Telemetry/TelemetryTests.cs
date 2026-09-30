using Husaynia.Application.Operations.Telemetry;
using System.Diagnostics.Metrics;

namespace Husaynia.Application.Tests.Operations.Telemetry;

public sealed class TelemetryTests
{
    [Fact]
    public void SyntheticCorrelationFlowsThroughNestedOperation()
    {
        var context = new CorrelationContext();

        using (context.Begin("synthetic-123", "test"))
        {
            var current = context.Current;
            Assert.Equal("synthetic-123", current.CorrelationId);
            Assert.Equal(32, current.TraceId.Length);
            Assert.NotNull(current.SpanId);
        }

        Assert.NotEqual("synthetic-123", context.Current.CorrelationId);
    }

    [Theory]
    [InlineData("Authorization", "Bearer top-secret")]
    [InlineData("card_number", "4242 4242 4242 4242")]
    [InlineData("donorEmail", "person@example.org")]
    [InlineData("form.payload", "{\"message\":\"private\"}")]
    public void SensitiveKeysAlwaysRedactEntireValue(string key, string value)
    {
        var redactor = new HostileSensitiveDataRedactor();

        Assert.Equal(HostileSensitiveDataRedactor.Redacted, redactor.Redact(key, value));
    }

    [Fact]
    public void FreeTextRedactsBearerJwtEmailSecretAssignmentsAndValidCardNumbers()
    {
        var redactor = new HostileSensitiveDataRedactor();
        const string hostile =
            "Bearer abc.def-123 user@example.org token=hunter2 " +
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature123 " +
            "4242-4242-4242-4242";

        var result = redactor.Redact("event", hostile);

        Assert.DoesNotContain("abc.def-123", result, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.org", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGci", result, StringComparison.Ordinal);
        Assert.DoesNotContain("4242-4242", result, StringComparison.Ordinal);
        Assert.Contains(HostileSensitiveDataRedactor.Redacted, result, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidExternalCorrelationIsNotReflected()
    {
        var context = new CorrelationContext();
        using var scope = context.Begin("bad\r\ntrace: injected", "test");

        Assert.DoesNotContain('\r', context.Current.CorrelationId);
        Assert.DoesNotContain('\n', context.Current.CorrelationId);
        Assert.NotEqual("bad\r\ntrace: injected", context.Current.CorrelationId);
    }

    [Theory]
    [InlineData(
        """{"outer":{"credentials":{"password":"nested-secret"},"items":[{"api%5Fkey":"encoded-secret"}]}}""",
        "nested-secret",
        "encoded-secret")]
    [InlineData(
        "safe=ok&pass%77ord=form-secret&next=%7B%22token%22%3A%22nested-token%22%7D",
        "form-secret",
        "nested-token")]
    public void NestedAndEncodedSensitiveValuesNeverLeak(
        string hostile,
        string firstSecret,
        string secondSecret)
    {
        var result = new HostileSensitiveDataRedactor().Redact("event", hostile);

        Assert.DoesNotContain(firstSecret, result, StringComparison.Ordinal);
        Assert.DoesNotContain(secondSecret, result, StringComparison.Ordinal);
        Assert.True(
            result.Contains(HostileSensitiveDataRedactor.Redacted, StringComparison.Ordinal) ||
            result.Contains(
                Uri.EscapeDataString(HostileSensitiveDataRedactor.Redacted),
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(""" {"password":"leading-space-secret"}""", "leading-space-secret")]
    [InlineData("\uFEFF{\"password\":\"bom-secret\"}", "bom-secret")]
    [InlineData("pass%2577ord=double-encoded-secret", "double-encoded-secret")]
    [InlineData("Bearer%20encoded-bearer-secret", "encoded-bearer-secret")]
    [InlineData(" %7B%22password%22%3A%22encoded-secret%22%7D", "encoded-secret")]
    [InlineData(
        "next=%25257B%252522password%252522%25253A%252522nested-json-secret%252522%25257D",
        "nested-json-secret")]
    [InlineData(
        "next=return%25253Fpass%25252577ord%25253Dnested-query-secret",
        "nested-query-secret")]
    public void RepeatedNormalizationNeverLeaksSensitiveValues(string hostile, string secret)
    {
        var result = new HostileSensitiveDataRedactor().Redact("event", hostile);

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains(
            HostileSensitiveDataRedactor.Redacted,
            Uri.UnescapeDataString(result),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MetricsCarryAmbientCorrelationTags()
    {
        var correlation = new CorrelationContext();
        string? observedCorrelation = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Husaynia.Operations")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, state) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "correlation.id")
                    {
                        observedCorrelation = tag.Value?.ToString();
                    }
                }
            });
        listener.Start();
        using var metrics = new OperationsMetrics(correlation);
        using (correlation.Begin("metric-correlation", "metric-test"))
        {
            metrics.RecordJob("definition", "completed", 1);
        }

        Assert.Equal("metric-correlation", observedCorrelation);
    }

    [Fact]
    public void DictionaryRedactionPreservesKeysButNeverSensitiveValues()
    {
        const string secret = "person@example.org";
        var values = new Dictionary<string, string?>
        {
            ["safe"] = $"Bearer hostile-token {secret}",
            ["donorEmail"] = secret,
            ["nested"] = """{"signature":"webhook-secret","display":"ok"}""",
        };

        var redacted = new HostileSensitiveDataRedactor().Redact(values);
        var serialized = string.Join("|", redacted.Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.Equal(values.Keys.Order(), redacted.Keys.Order());
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("hostile-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("webhook-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void NumericPaymentCardInsideJsonIsRedacted()
    {
        const string card = "4242424242424242";

        var result = new HostileSensitiveDataRedactor().Redact(
            "event",
            $$"""{"number":{{card}},"safe":123}""");

        Assert.DoesNotContain(card, result, StringComparison.Ordinal);
        Assert.Contains(HostileSensitiveDataRedactor.Redacted, result, StringComparison.Ordinal);
        Assert.Contains("123", result, StringComparison.Ordinal);
    }
}
