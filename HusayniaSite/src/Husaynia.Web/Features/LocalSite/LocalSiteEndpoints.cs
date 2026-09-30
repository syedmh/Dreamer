using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Antiforgery;

namespace Husaynia.Web.Features.LocalSite;

public static class LocalSiteEndpoints
{
    public static IEndpointRouteBuilder MapLocalSite(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/", (LocalSiteStore store) =>
            Html(LocalSiteRenderer.Page("Home", LocalSiteRenderer.Home(store), "/")));
        endpoints.MapGet("/overview", (LocalSiteStore store) =>
            ContentPage(store, "overview"));
        endpoints.MapGet("/build-husaynia", (LocalSiteStore store) =>
            ContentPage(store, "build-husaynia"));
        endpoints.MapGet("/duas", (LocalSiteStore store) =>
            ContentPage(store, "duas"));
        endpoints.MapGet("/dua-kumayl", (LocalSiteStore store) =>
            ContentPage(store, "dua-kumayl"));
        endpoints.MapGet("/privacy-policy", (LocalSiteStore store) =>
            ContentPage(store, "privacy-policy"));
        endpoints.MapGet("/terms-and-conditions", (LocalSiteStore store) =>
            ContentPage(store, "terms-and-conditions"));
        endpoints.MapGet("/prayer-timings", (LocalSiteStore store) =>
            Html(LocalSiteRenderer.Page(
                "Prayer Timings",
                LocalSiteRenderer.Prayer(store),
                "/prayer-timings")));
        endpoints.MapGet("/programs", (LocalSiteStore store) =>
            Html(LocalSiteRenderer.Page(
                "Programs",
                LocalSiteRenderer.Programs(store, "Programs"),
                "/programs")));
        endpoints.MapGet("/islamic-calendar", (LocalSiteStore store) =>
            Html(LocalSiteRenderer.Page(
                "Islamic Calendar",
                LocalSiteRenderer.Programs(store, "Islamic Calendar"),
                "/islamic-calendar")));
        endpoints.MapGet("/events", () =>
            Results.Redirect("/islamic-calendar", permanent: true));
        endpoints.MapGet("/event/{slug}", (string slug, LocalSiteStore store) =>
        {
            var item = store.Events.FirstOrDefault(candidate =>
                string.Equals(candidate.Slug, slug, StringComparison.OrdinalIgnoreCase));
            return item is null
                ? Results.NotFound()
                : Html(LocalSiteRenderer.Page(
                    item.Title,
                    LocalSiteRenderer.Event(item),
                    $"/event/{item.Slug}"));
        });
        endpoints.MapGet("/announcements", (LocalSiteStore store) =>
            Html(LocalSiteRenderer.Page(
                "Announcements",
                LocalSiteRenderer.Social(store),
                "/announcements")));
        endpoints.MapGet("/photos", (LocalSiteStore store) =>
            Html(LocalSiteRenderer.Page(
                "Photos",
                LocalSiteRenderer.Media(store, "Photos"),
                "/photos")));
        endpoints.MapGet("/videos", (LocalSiteStore store) =>
            Html(LocalSiteRenderer.Page(
                "Videos",
                LocalSiteRenderer.Media(store, "Videos") +
                "<div class=\"empty-state\"><h2>Video provider disabled</h2><p>Production video links remain gated on approved content evidence.</p></div>",
                "/videos")));
        endpoints.MapGet("/search", (string? q, LocalSiteStore store) =>
            Html(LocalSiteRenderer.Page(
                "Search",
                LocalSiteRenderer.Search(store, q ?? string.Empty),
                "/search",
                includeSearch: false)));

        endpoints.MapGet("/contact-us", (
            HttpContext context,
            IAntiforgery antiforgery,
            string? receipt) =>
            Html(LocalSiteRenderer.Page(
                "Contact",
                LocalSiteRenderer.Form(
                    context,
                    antiforgery,
                    "contact",
                    "Contact Husaynia",
                    receipt ?? string.Empty),
                "/contact-us")));
        endpoints.MapGet("/pledge-construction", (
            HttpContext context,
            IAntiforgery antiforgery,
            string? receipt) =>
            Html(LocalSiteRenderer.Page(
                "Pledge",
                LocalSiteRenderer.Form(
                    context,
                    antiforgery,
                    "pledge",
                    "Construction Pledge",
                    receipt ?? string.Empty),
                "/pledge-construction")));
        endpoints.MapPost("/local/forms/{formKey}", SubmitFormAsync);

        endpoints.MapGet("/donate", (
            HttpContext context,
            IAntiforgery antiforgery,
            string? state) =>
            Html(LocalSiteRenderer.Page(
                "Donate",
                LocalSiteRenderer.Donation(
                    context,
                    antiforgery,
                    state ?? string.Empty,
                    "general"),
                "/donate")));
        endpoints.MapGet("/donate/{category}", (
            HttpContext context,
            IAntiforgery antiforgery,
            string category,
            string? state) =>
        {
            if (!LocalDonationCategories.IsSupported(category))
            {
                return Results.NotFound();
            }

            return Html(LocalSiteRenderer.Page(
                $"Donate: {category}",
                LocalSiteRenderer.Donation(
                    context,
                    antiforgery,
                    state ?? string.Empty,
                    category),
                $"/donate/{category}"));
        });
        endpoints.MapPost("/local/donations/simulate", SimulateDonationAsync);

        endpoints.MapGet("/admin", (
            HttpContext context,
            IAntiforgery antiforgery,
            LocalSiteStore store,
            string? message) =>
        {
            if (!IsLocalRequest(context))
            {
                return Results.NotFound();
            }

            return Html(LocalSiteRenderer.Page(
                "Local Admin",
                LocalSiteRenderer.Admin(
                    context,
                    antiforgery,
                    store,
                    message ?? string.Empty),
                "/admin"));
        });
        endpoints.MapPost(
            "/admin/local/content/{key}/toggle",
            ToggleContentAsync);

        endpoints.MapGet("/calendar.ics", (LocalSiteStore store) =>
            Results.Text(
                LocalSiteRenderer.Ical(store),
                "text/calendar; charset=utf-8"));
        endpoints.MapGet("/sitemap.xml", (LocalSiteStore store) =>
            Results.Text(
                LocalSiteRenderer.Sitemap(store),
                "application/xml; charset=utf-8"));
        endpoints.MapGet("/robots.txt", () =>
            Results.Text(
                "User-agent: *\nDisallow: /\n",
                "text/plain; charset=utf-8"));
        endpoints.MapGet("/health/live", () => Results.Json(new
        {
            status = "healthy",
            mode = "local-synthetic",
            externalDependencies = "disabled",
        }));
        endpoints.MapGet("/health/ready", () => Results.Json(new
        {
            status = "degraded",
            mode = "local-synthetic",
            database = "disabled",
            payments = "sandbox-simulation",
            forms = "in-memory-no-delivery",
        }));

        return endpoints;
    }

    private static IResult ContentPage(LocalSiteStore store, string slug)
    {
        var page = store.Pages.Single(item =>
            string.Equals(item.Slug, slug, StringComparison.Ordinal));
        return Html(LocalSiteRenderer.Page(
            page.Title,
            LocalSiteRenderer.Content(page),
            $"/{page.Slug}"));
    }

    private static async Task<IResult> SubmitFormAsync(
        HttpContext context,
        string formKey,
        IAntiforgery antiforgery,
        LocalSiteStore store)
    {
        if (formKey is not ("contact" or "pledge"))
        {
            return Results.NotFound();
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(context, antiforgery)
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(form["website"]))
        {
            return Results.BadRequest("Submission rejected.");
        }

        var name = form["name"].ToString().Trim();
        var email = form["email"].ToString().Trim();
        var message = form["message"].ToString().Trim();
        var consent = form["consent"].ToString();
        if (name.Length is < 1 or > 100 ||
            email.Length is < 3 or > 254 ||
            !email.Contains('@', StringComparison.Ordinal) ||
            message.Length is < 1 or > 2_000 ||
            !string.Equals(consent, "yes", StringComparison.Ordinal))
        {
            return Results.BadRequest("Please complete all required fields with valid values.");
        }

        if (formKey == "pledge" &&
            (!decimal.TryParse(
                    form["amount"],
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var amount) ||
             amount is < 1 or > 100_000))
        {
            return Results.BadRequest("Enter a valid illustrative pledge amount.");
        }

        var receipt = store.RecordFormReceipt(formKey);
        var path = formKey == "contact" ? "/contact-us" : "/pledge-construction";
        return Results.Redirect($"{path}?receipt={WebUtility.UrlEncode(receipt)}", permanent: false);
    }

    private static async Task<IResult> SimulateDonationAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        var antiforgeryFailure = await ValidateAntiforgeryAsync(context, antiforgery)
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted)
            .ConfigureAwait(false);
        var category = form["category"].ToString();
        if (!LocalDonationCategories.IsSupported(category))
        {
            return Results.BadRequest("Choose a supported donation category.");
        }

        if (!decimal.TryParse(
                form["amount"],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var amount) ||
            amount is < 1 or > 100_000)
        {
            return Results.BadRequest("Enter a valid sandbox amount.");
        }

        var outcome = form["outcome"].ToString();
        if (outcome is not ("success" or "processing" or "cancelled"))
        {
            return Results.BadRequest("Choose a supported sandbox outcome.");
        }

        return Results.Redirect(
            $"/donate/{WebUtility.UrlEncode(category)}?state={WebUtility.UrlEncode(outcome)}",
            permanent: false);
    }

    private static async Task<IResult> ToggleContentAsync(
        HttpContext context,
        string key,
        IAntiforgery antiforgery,
        LocalSiteStore store)
    {
        if (!IsLocalRequest(context) ||
            !store.PublicationState.ContainsKey(key))
        {
            return Results.NotFound();
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(context, antiforgery)
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var published = store.TogglePublication(key);
        return Results.Redirect(
            $"/admin?message={WebUtility.UrlEncode($"{key} is now {(published ? "published" : "draft")}.")}",
            permanent: false);
    }

    private static async Task<IResult?> ValidateAntiforgeryAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest("The request verification token is missing or invalid.");
        }
    }

    private static bool IsLocalRequest(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is null || IPAddress.IsLoopback(address);
    }

    private static IResult Html(string content) =>
        Results.Content(content, "text/html; charset=utf-8");
}
