using Husaynia.BaselineCapture;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class RobotsPolicyTests
{
    [Fact]
    public void ParseHonorsWildcardDisallowRules()
    {
        var policy = RobotsPolicy.Parse("""
            User-agent: *
            Allow: /wp-admin/admin-ajax.php
            Disallow: /wp-admin/
            Disallow: /wp-content/uploads/wpforms/
            """);

        Assert.False(policy.IsAllowed(new Uri("https://www.husaynia.org/wp-admin/edit.php")));
        Assert.True(policy.IsAllowed(new Uri("https://www.husaynia.org/wp-admin/admin-ajax.php")));
        Assert.False(policy.IsAllowed(new Uri("https://www.husaynia.org/wp-content/uploads/wpforms/private.pdf")));
        Assert.True(policy.IsAllowed(new Uri("https://www.husaynia.org/contact-us/")));
    }

    [Fact]
    public void ParseRejectsOversizedRobotsText()
    {
        var exception = Assert.Throws<CaptureSafetyException>(
            () => RobotsPolicy.Parse(
                new string('x', RobotsPolicy.MaximumTextCharacters + 1)));

        Assert.Equal("robots-text-too-large", exception.Message);
    }

    [Fact]
    public void ParseRejectsExcessiveRuleCount()
    {
        var text = string.Join(
            '\n',
            Enumerable.Repeat(
                "Disallow: /bounded",
                RobotsPolicy.MaximumRuleCount + 1).Prepend("User-agent: *"));

        var exception = Assert.Throws<CaptureSafetyException>(
            () => RobotsPolicy.Parse(text));

        Assert.Equal("robots-rule-limit-exceeded", exception.Message);
    }

    [Fact]
    public void ParseRejectsOversizedPattern()
    {
        var exception = Assert.Throws<CaptureSafetyException>(
            () => RobotsPolicy.Parse(
                $"User-agent: *\nDisallow: /{new string('x', RobotsPolicy.MaximumPatternCharacters)}"));

        Assert.Equal("robots-pattern-too-long", exception.Message);
    }

    [Theory]
    [InlineData("/private/*/secret$", "/private/alpha/secret", false)]
    [InlineData("/private/*/secret$", "/private/alpha/secret/more", true)]
    [InlineData("/private/*$", "/private/alpha/secret", false)]
    [InlineData("*/secret$", "/private/secret/secret", false)]
    [InlineData("/downloads/*.zip", "/downloads/archive.zip?source=site", false)]
    public void WildcardMatchingIsDeterministicAndHonorsEndAnchor(
        string pattern,
        string pathAndQuery,
        bool expectedAllowed)
    {
        var policy = RobotsPolicy.Parse(
            $"User-agent: *\nDisallow: {pattern}");

        Assert.Equal(
            expectedAllowed,
            policy.IsAllowed(new Uri($"https://www.husaynia.org{pathAndQuery}")));
    }

    [Fact]
    public async Task CliRefusesCaptureWithoutNoSubmitFlag()
    {
        var exitCode = await Program.Main(["capture"]);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task CliRefusesUnapprovedBaselinePromotion()
    {
        var exitCode = await Program.Main(["promote", "--from", "staging", "--to", "baseline"]);
        Assert.Equal(2, exitCode);
    }

    [Theory]
    [InlineData("http://www.husaynia.org/")]
    [InlineData("https://www.husaynia.org:444/")]
    [InlineData("https://user:password@www.husaynia.org/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://husaynia.org/")]
    public async Task CliRefusesAnyBaseUrlOutsideTheFixedTrustBoundary(string baseUrl)
    {
        var output = Path.Combine(Path.GetTempPath(), $"husaynia-refusal-{Guid.NewGuid():N}");
        var exitCode = await Program.Main(
            ["capture", "--no-submit", "--base-url", baseUrl, "--output", output]);
        Assert.Equal(2, exitCode);
        Assert.False(Directory.Exists(output));
    }

}
