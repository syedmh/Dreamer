using System.Net;
using Husaynia.Web.Features.LocalSite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Husaynia.IntegrationTests.LocalReadiness;

public sealed class LocalSiteTests
{
    private static readonly string[] RepresentativeRoutes =
    [
        "/",
        "/overview",
        "/prayer-timings",
        "/programs",
        "/islamic-calendar",
        "/event/friday-prayers",
        "/announcements",
        "/photos",
        "/videos",
        "/duas",
        "/dua-kumayl",
        "/build-husaynia",
        "/donate",
        "/contact-us",
        "/pledge-construction",
        "/privacy-policy",
        "/terms-and-conditions",
        "/admin",
    ];

    [Fact]
    public async Task RepresentativeRoutesRenderWithoutExternalServices()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var route in RepresentativeRoutes)
        {
            using var response = await client.GetAsync(route);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Development fixture", body, StringComparison.Ordinal);
            Assert.DoesNotContain("https://", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task HomeHasRequiredNavigationStatesAndStrictSecurityHeaders()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Prayer Timings", body, StringComparison.Ordinal);
        Assert.Contains("Islamic Calendar", body, StringComparison.Ordinal);
        Assert.Contains("Donation sandbox", body, StringComparison.Ordinal);
        Assert.Contains("Social embeds disabled", body, StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", Header(response, "Content-Security-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task SearchReturnsPublishedFixtureResultsAndEncodesInput()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var results = await client.GetStringAsync("/search?q=community");
        var hostile = await client.GetStringAsync(
            "/search?q=%3Cscript%3Ealert(1)%3C%2Fscript%3E");

        Assert.Contains("Community Service Day", results, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", hostile, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", hostile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VideosShowsDedicatedUnavailableStateWithoutPhotoOrAudioCards()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var page = await client.GetStringAsync("/videos");

        Assert.Contains("<h1>Videos</h1>", page, StringComparison.Ordinal);
        Assert.Contains("Video provider disabled", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Community hall", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Monday rites audio", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContactFormRequiresAntiforgeryAndReturnsLocalReceipt()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        using var rejected = await client.PostAsync(
            "/local/forms/contact",
            new FormUrlEncodedContent(ValidContactFields()));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var formPage = await client.GetStringAsync("/contact-us");
        var fields = ValidContactFields();
        fields["__RequestVerificationToken"] = ExtractToken(formPage);
        using var accepted = await client.PostAsync(
            "/local/forms/contact",
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.StartsWith(
            "/contact-us?receipt=LOCAL-",
            accepted.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DonationSandboxSimulatesProcessingWithoutPaymentFields()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var page = await client.GetStringAsync("/donate/programs");
        Assert.DoesNotContain("card number", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no payment gateway registration", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "<option value=\"programs\" selected>Programs</option>",
            page,
            StringComparison.Ordinal);

        using var response = await client.PostAsync(
            "/local/donations/simulate",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(page),
                ["category"] = "programs",
                ["amount"] = "25",
                ["outcome"] = "processing",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/donate/programs?state=processing",
            response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task LocalAdminTransitionIsAntiforgeryProtectedAndAudited()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var page = await client.GetStringAsync("/admin");
        Assert.Contains("community-update", page, StringComparison.Ordinal);
        Assert.Contains("Published", page, StringComparison.Ordinal);

        using var response = await client.PostAsync(
            "/admin/local/content/community-update/toggle",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = ExtractToken(page),
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var updated = await client.GetStringAsync("/admin");
        var announcements = await client.GetStringAsync("/announcements");
        Assert.Contains("local.content.unpublish", updated, StringComparison.Ordinal);
        Assert.Contains("in-memory", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Arbaeen community program", announcements, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalContractEndpointsAreDeterministicAndNonIndexable()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var calendar = await client.GetStringAsync("/calendar.ics");
        var sitemap = await client.GetStringAsync("/sitemap.xml");
        var robots = await client.GetStringAsync("/robots.txt");
        var health = await client.GetStringAsync("/health/ready");

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", calendar, StringComparison.Ordinal);
        Assert.Contains("UID:friday-prayers@local.husaynia.test", calendar, StringComparison.Ordinal);
        Assert.Contains("<loc>http://localhost/prayer-timings</loc>", sitemap, StringComparison.Ordinal);
        Assert.Equal("User-agent: *\nDisallow: /\n", robots);
        Assert.Contains("\"payments\":\"sandbox-simulation\"", health, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsAliasIsOnePermanentRedirect()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/events");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/islamic-calendar", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public void LocalModeRefusesToStartOutsideDevelopment()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalSiteOptions.EnsureEnvironment(
                enabled: true,
                new TestHostEnvironment("Production")));
        Assert.Contains(
            "only in the Development environment",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting(LocalSiteOptions.EnabledKey, "true");
            });

    private static Dictionary<string, string> ValidContactFields() =>
        new(StringComparer.Ordinal)
        {
            ["name"] = "Local Tester",
            ["email"] = "tester@example.test",
            ["message"] = "Please confirm the local workflow.",
            ["consent"] = "yes",
            ["website"] = string.Empty,
        };

    private static string ExtractToken(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Antiforgery token field was not found.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "Antiforgery token value was not terminated.");
        return WebUtility.HtmlDecode(html[start..end]);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        string.Join(",", response.Headers.GetValues(name));

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Husaynia.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
