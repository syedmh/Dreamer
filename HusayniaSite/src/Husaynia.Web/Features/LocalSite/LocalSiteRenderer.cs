using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;

namespace Husaynia.Web.Features.LocalSite;

internal static class LocalSiteRenderer
{
    private static readonly string[] SitemapPaths =
    [
        "/", "/overview", "/prayer-timings", "/programs", "/islamic-calendar",
        "/announcements", "/photos", "/videos", "/duas", "/dua-kumayl",
        "/build-husaynia", "/donate", "/contact-us", "/pledge-construction",
        "/privacy-policy", "/terms-and-conditions",
    ];

    internal static string Page(
        string title,
        string body,
        string path,
        bool includeSearch = true) =>
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <title>{{Encode(title)}} | Husaynia Local</title>
          <meta name="description" content="Synthetic local Husaynia site fixture for safe development testing.">
          <meta name="robots" content="noindex,nofollow">
          <link rel="canonical" href="http://localhost{{Encode(path)}}">
          <link rel="stylesheet" href="/css/site.css">
          <script src="/js/site.js" defer></script>
        </head>
        <body>
          <a class="skip-link" href="#main">Skip to content</a>
          <div class="local-banner" role="status">Development fixture · synthetic content · no live services</div>
          <header class="site-header">
            <a class="brand" href="/" aria-label="Husaynia local home">
              <span class="brand-mark" aria-hidden="true">ح</span>
              <span><strong>Husaynia</strong><small>Islamic Society of Seattle</small></span>
            </a>
            <button class="menu-toggle" type="button" aria-expanded="false" aria-controls="site-nav">Menu</button>
            <nav id="site-nav" aria-label="Primary navigation">
              <a href="/">Home</a>
              <a href="/overview">Overview</a>
              <a href="/prayer-timings">Prayer Timings</a>
              <a href="/programs">Programs</a>
              <a href="/islamic-calendar">Islamic Calendar</a>
              <a href="/duas">Duas</a>
              <a href="/photos">Media</a>
              <a href="/donate">Donate</a>
              <a href="/contact-us">Contact</a>
            </nav>
            {{(includeSearch ? SearchForm() : string.Empty)}}
          </header>
          <main id="main">{{body}}</main>
          <footer>
            <div><strong>Husaynia Local</strong><p>Representative development experience for local testing.</p></div>
            <nav aria-label="Footer navigation">
              <a href="/privacy-policy">Privacy</a>
              <a href="/terms-and-conditions">Terms</a>
              <a href="/admin">Local admin</a>
              <a href="/health/live">Health</a>
            </nav>
            <p>External social, analytics, email, storage, payment, and production networks are disabled.</p>
          </footer>
        </body>
        </html>
        """;

    internal static string Home(LocalSiteStore store)
    {
        var nextEvent = store.Events[0];
        return """
          <section class="hero">
            <div>
              <p class="eyebrow">Faith · community · service</p>
              <h1>A complete, safe local Husaynia experience</h1>
              <p>Explore representative programs, prayer schedules, religious resources, media, forms, and donation states without contacting production systems.</p>
              <div class="actions"><a class="button" href="/programs">View programs</a><a class="button secondary" href="/donate">Donation sandbox</a></div>
            </div>
            <img src="/images/community-hall.svg" alt="Illustrated community hall placeholder">
          </section>
          <section class="quick-grid" aria-label="Quick links">
            <a href="/prayer-timings"><strong>Prayer timings</strong><span>Today and monthly state</span></a>
            <a href="/islamic-calendar"><strong>Islamic calendar</strong><span>Events and iCal</span></a>
            <a href="/duas"><strong>Duas and rites</strong><span>Arabic and translation layout</span></a>
            <a href="/build-husaynia"><strong>Build Husaynia</strong><span>Representative project page</span></a>
          </section>
        """ +
        CardSection(
            "Upcoming program",
            EventCard(nextEvent)) +
        CardSection(
            "Community announcements",
            string.Join(string.Empty, store.PublishedSocial.Select(SocialCard))) +
        """
          <section class="callout">
            <div><p class="eyebrow">Local readiness</p><h2>Social embeds disabled</h2></div>
            <p>Forms use an in-memory sink, donations simulate browser states, media is repository-owned, and social content is a synthetic persisted snapshot.</p>
          </section>
        """;
    }

    internal static string Content(LocalPage page) =>
        $$"""
        <article class="content-page">
          <p class="eyebrow">{{Encode(page.Eyebrow)}}</p>
          <h1>{{Encode(page.Title)}}</h1>
          <p class="lede">{{Encode(page.Summary)}}</p>
          {{Paragraphs(page.Body)}}
          <p class="fixture-note">Synthetic local fixture. Production wording remains gated on approved source evidence.</p>
        </article>
        """;

    internal static string Programs(LocalSiteStore store, string title) =>
        $$"""
        <section class="page-heading"><p class="eyebrow">Gather and learn</p><h1>{{Encode(title)}}</h1><p>Representative current and future programs in America/Los_Angeles.</p></section>
        <section class="card-grid">{{string.Join(string.Empty, store.Events.Select(EventCard))}}</section>
        <p><a class="button secondary" href="/calendar.ics">Download local iCal fixture</a></p>
        """;

    internal static string Event(LocalEvent item) =>
        $$"""
        <article class="content-page">
          <p class="eyebrow">{{Encode(item.Category)}}</p>
          <h1>{{Encode(item.Title)}}</h1>
          <dl class="details">
            <div><dt>Date</dt><dd>{{item.StartsAt.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)}}</dd></div>
            <div><dt>Time</dt><dd>{{item.StartsAt.ToString("h:mm tt", CultureInfo.InvariantCulture)}} Pacific</dd></div>
            <div><dt>Location</dt><dd>{{Encode(item.Location)}}</dd></div>
          </dl>
          <p>{{Encode(item.Summary)}}</p>
          <a class="button secondary" href="/calendar.ics">Download calendar</a>
        </article>
        """;

    internal static string Prayer(LocalSiteStore store) =>
        $$"""
        <section class="page-heading"><p class="eyebrow">Snohomish · Pacific time</p><h1>Prayer Timings</h1><p>Deterministic synthetic values for layout and local behavior testing.</p></section>
        <div class="status-card"><strong>Local calculated fixture</strong><span>Provider calls disabled · America/Los_Angeles · not authoritative</span></div>
        <div class="prayer-grid">{{string.Join(string.Empty, store.Prayers.Select(PrayerCard))}}</div>
        <section class="content-page"><h2>Monthly schedule state</h2><p>The production module supports snapshots, stale labeling, overrides, and provider isolation. Local mode presents a complete synthetic day while production profile values remain unset.</p></section>
        """;

    internal static string Media(LocalSiteStore store, string kind) =>
        $$"""
        <section class="page-heading"><p class="eyebrow">Community media</p><h1>{{Encode(kind)}}</h1><p>Repository-owned placeholders exercise responsive cards, alternative text, and unavailable audio states.</p></section>
        <section class="media-grid">{{string.Join(string.Empty, store.Media.Where(item => kind == "Media" || string.Equals(item.Kind + "s", kind, StringComparison.OrdinalIgnoreCase)).Select(MediaCard))}}</section>
        """;

    internal static string Social(LocalSiteStore store) =>
        $$"""
        <section class="page-heading"><p class="eyebrow">Announcements</p><h1>Community updates</h1><p>Persisted synthetic snapshots render without third-party scripts or tracking.</p></section>
        <section class="card-grid">{{string.Join(string.Empty, store.PublishedSocial.Select(SocialCard))}}</section>
        <div class="status-card"><strong>Social embeds disabled</strong><span>No consent or provider credentials are present in local mode.</span></div>
        """;

    internal static string Search(LocalSiteStore store, string query)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0)
        {
            return """
              <section class="page-heading"><p class="eyebrow">Search</p><h1>Find public content</h1><p>Enter a word or phrase. Empty searches do not query any dependency.</p></section>
            """ + SearchForm(large: true);
        }

        var pageResults = store.Pages
            .Where(page => Contains(page.Title, normalized) ||
                Contains(page.Summary, normalized) ||
                page.Tags.Any(tag => Contains(tag, normalized)))
            .Select(page => (page.Title, page.Summary, $"/{page.Slug}", "Page"));
        var eventResults = store.Events
            .Where(item => Contains(item.Title, normalized) ||
                Contains(item.Summary, normalized) ||
                Contains(item.Category, normalized))
            .Select(item => (item.Title, item.Summary, $"/event/{item.Slug}", "Event"));
        var socialResults = store.PublishedSocial
            .Where(item => Contains(item.Title, normalized) ||
                Contains(item.Summary, normalized))
            .Select(item => (item.Title, item.Summary, "/announcements", "Announcement"));
        var results = pageResults
            .Concat(eventResults)
            .Concat(socialResults)
            .Take(20)
            .ToArray();

        return $$"""
          <section class="page-heading"><p class="eyebrow">Search</p><h1>Results for “{{Encode(normalized)}}”</h1><p>{{results.Length}} published local fixture result(s).</p></section>
          {{SearchForm(normalized, large: true)}}
          <section class="search-results">
            {{(results.Length == 0 ? "<div class=\"empty-state\"><h2>No results</h2><p>Try a broader term such as community, dua, or prayer.</p></div>" : string.Join(string.Empty, results.Select(SearchCard)))}}
          </section>
        """;
    }

    internal static string Form(
        HttpContext context,
        IAntiforgery antiforgery,
        string formKey,
        string title,
        string receipt = "",
        string error = "")
    {
        var token = antiforgery.GetAndStoreTokens(context).RequestToken ??
            throw new InvalidOperationException("Antiforgery token was not created.");
        var status = receipt.Length > 0
            ? $"<div class=\"success\" role=\"status\"><h2>Submission accepted</h2><p>Receipt {Encode(receipt)}. Nothing was emailed or persisted.</p></div>"
            : error.Length > 0
                ? $"<div class=\"error\" role=\"alert\">{Encode(error)}</div>"
                : string.Empty;
        return $$"""
          <section class="page-heading"><p class="eyebrow">Safe local form</p><h1>{{Encode(title)}}</h1><p>Validation and acknowledgement are active. Delivery is disabled and values are not retained.</p></section>
          {{status}}
          <form class="site-form" method="post" action="/local/forms/{{Encode(formKey)}}">
            <input type="hidden" name="__RequestVerificationToken" value="{{Encode(token)}}">
            <div class="honeypot" aria-hidden="true"><label>Website<input name="website" tabindex="-1" autocomplete="off"></label></div>
            <label>Name <input name="name" required maxlength="100" autocomplete="name"></label>
            <label>Email <input name="email" type="email" required maxlength="254" autocomplete="email"></label>
            {{(formKey == "pledge" ? "<label>Illustrative pledge amount <input name=\"amount\" type=\"number\" min=\"1\" max=\"100000\" value=\"100\"></label>" : string.Empty)}}
            <label>Message <textarea name="message" required maxlength="2000" rows="6"></textarea></label>
            <label class="check"><input name="consent" type="checkbox" value="yes" required> I understand this is a local synthetic submission with no delivery.</label>
            <button class="button" type="submit">Submit locally</button>
          </form>
        """;
    }

    internal static string Donation(
        HttpContext context,
        IAntiforgery antiforgery,
        string state,
        string category)
    {
        var token = antiforgery.GetAndStoreTokens(context).RequestToken ??
            throw new InvalidOperationException("Antiforgery token was not created.");
        var categoryOptions = string.Join(
            string.Empty,
            LocalDonationCategories.All.Select(pair =>
                $"<option value=\"{Encode(pair.Key)}\"{(string.Equals(pair.Key, category, StringComparison.OrdinalIgnoreCase) ? " selected" : string.Empty)}>{Encode(pair.Value)}</option>"));
        var status = state switch
        {
            "success" => "<div class=\"success\" role=\"status\"><h2>Sandbox success</h2><p>A simulated verified state is displayed. No donation record or campaign total was changed.</p></div>",
            "cancelled" => "<div class=\"error\" role=\"status\"><h2>Sandbox cancelled</h2><p>No payment was created. You can safely retry the simulation.</p></div>",
            "processing" => "<div class=\"status-card\" role=\"status\"><strong>Sandbox processing</strong><span>The success page remains read-only while a real implementation would await a verified webhook.</span></div>",
            _ => string.Empty,
        };
        return $$"""
          <section class="page-heading"><p class="eyebrow">Donation sandbox</p><h1>Support Husaynia</h1><p>Exercise amount, category, anonymity, success, cancellation, and processing states without Stripe or a real payment.</p></section>
          {{status}}
          <form class="site-form" method="post" action="/local/donations/simulate">
            <input type="hidden" name="__RequestVerificationToken" value="{{Encode(token)}}">
            <label>Category <select name="category">{{categoryOptions}}</select></label>
            <label>Amount (USD) <input name="amount" type="number" min="1" max="100000" value="50" required></label>
            <label class="check"><input name="anonymous" type="checkbox" value="yes"> Donate anonymously</label>
            <label>Simulated outcome <select name="outcome"><option value="success">Success</option><option value="processing">Processing</option><option value="cancelled">Cancelled</option></select></label>
            <button class="button" type="submit">Simulate checkout</button>
          </form>
          <div class="fixture-note"><strong>Safety:</strong> local mode has no payment gateway registration, keys, outbound HTTP, or card fields.</div>
        """;
    }

    internal static string Admin(
        HttpContext context,
        IAntiforgery antiforgery,
        LocalSiteStore store,
        string message = "")
    {
        var token = antiforgery.GetAndStoreTokens(context).RequestToken ??
            throw new InvalidOperationException("Antiforgery token was not created.");
        var rows = string.Join(string.Empty, store.PublicationState.Select(pair =>
            $$"""
            <tr><td>{{Encode(pair.Key)}}</td><td><span class="badge">{{(pair.Value ? "Published" : "Draft")}}</span></td><td>
              <form method="post" action="/admin/local/content/{{Encode(pair.Key)}}/toggle">
                <input type="hidden" name="__RequestVerificationToken" value="{{Encode(token)}}">
                <button class="button small" type="submit">{{(pair.Value ? "Unpublish" : "Publish")}}</button>
              </form>
            </td></tr>
            """));
        var auditRows = string.Join(string.Empty, store.Audit.Select(entry =>
            $"<li><time>{entry.OccurredAt:HH:mm:ss}Z</time> {Encode(entry.Action)} · {Encode(entry.Target)} · {Encode(entry.Outcome)}</li>"));
        return $$"""
          <section class="page-heading"><p class="eyebrow">Loopback-only safe workflow</p><h1>Local Admin</h1><p>In-memory publish/unpublish exercises antiforgery, state transitions, and audit presentation. Restarting clears every change.</p></section>
          {{(message.Length > 0 ? $"<div class=\"success\" role=\"status\">{Encode(message)}</div>" : string.Empty)}}
          <div class="admin-grid">
            <section class="content-page"><h2>Content lifecycle</h2><table><thead><tr><th>Content</th><th>State</th><th>Action</th></tr></thead><tbody>{{rows}}</tbody></table></section>
            <section class="content-page"><h2>Audit outcomes</h2><ul class="audit-list">{{(auditRows.Length == 0 ? "<li>No local changes yet.</li>" : auditRows)}}</ul></section>
          </div>
          <div class="fixture-note">Production identity, MFA, durable audit, database mutations, and external integrations are intentionally not activated in local fixture mode.</div>
        """;
    }

    internal static string Sitemap(LocalSiteStore store)
    {
        var paths = SitemapPaths.Concat(
            store.Events.Select(item => $"/event/{item.Slug}"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
            string.Join(string.Empty, paths.Select(path =>
                $"<url><loc>http://localhost{WebUtility.HtmlEncode(path)}</loc></url>")) +
            "</urlset>";
    }

    internal static string Ical(LocalSiteStore store)
    {
        var builder = new StringBuilder();
        builder.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Husaynia Local//Fixture//EN\r\n");
        foreach (var item in store.Events.OrderBy(item => item.StartsAt))
        {
            builder.Append("BEGIN:VEVENT\r\n");
            builder.Append(CultureInfo.InvariantCulture, $"UID:{item.Slug}@local.husaynia.test\r\n");
            builder.Append(CultureInfo.InvariantCulture, $"DTSTART:{item.StartsAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\n");
            builder.Append(CultureInfo.InvariantCulture, $"SUMMARY:{EscapeIcal(item.Title)}\r\n");
            builder.Append(CultureInfo.InvariantCulture, $"LOCATION:{EscapeIcal(item.Location)}\r\n");
            builder.Append(CultureInfo.InvariantCulture, $"URL:http://localhost/event/{item.Slug}\r\n");
            builder.Append("END:VEVENT\r\n");
        }

        builder.Append("END:VCALENDAR\r\n");
        return builder.ToString();
    }

    private static string SearchForm(
        string query = "",
        bool large = false) =>
        $$"""
        <form class="search-form{{(large ? " large" : string.Empty)}}" action="/search" method="get" role="search">
          <label><span class="visually-hidden">Search</span><input name="q" value="{{Encode(query)}}" maxlength="100" placeholder="Search public content"></label>
          <button type="submit">Search</button>
        </form>
        """;

    private static string CardSection(string title, string cards) =>
        $"<section><div class=\"section-heading\"><h2>{Encode(title)}</h2></div><div class=\"card-grid\">{cards}</div></section>";

    private static string EventCard(LocalEvent item) =>
        $$"""
        <article class="card">
          <p class="eyebrow">{{Encode(item.Category)}}</p>
          <h3><a href="/event/{{Encode(item.Slug)}}">{{Encode(item.Title)}}</a></h3>
          <p><strong>{{item.StartsAt.ToString("MMM d · h:mm tt", CultureInfo.InvariantCulture)}}</strong><br>{{Encode(item.Location)}}</p>
          <p>{{Encode(item.Summary)}}</p>
        </article>
        """;

    private static string SocialCard(LocalSocialItem item) =>
        $$"""
        <article class="card">
          <p class="eyebrow">{{Encode(item.Source)}} · {{item.PublishedOn.ToString("MMM d", CultureInfo.InvariantCulture)}}</p>
          <h3>{{Encode(item.Title)}}</h3>
          <p>{{Encode(item.Summary)}}</p>
          <span class="badge">Synthetic snapshot</span>
        </article>
        """;

    private static string PrayerCard(LocalPrayer item) =>
        $"<article><span>{Encode(item.Name)}</span><strong>{Encode(item.Time)}</strong><small>{Encode(item.Note)}</small></article>";

    private static string MediaCard(LocalMediaItem item) =>
        $$"""
        <article class="media-card">
          {{(item.AssetPath.Length > 0 ? $"<img src=\"{Encode(item.AssetPath)}\" alt=\"{Encode(item.Description)}\">" : "<div class=\"media-unavailable\" role=\"img\" aria-label=\"Audio unavailable\"><span>Audio</span><strong>Awaiting approved media</strong></div>")}}
          <div><p class="eyebrow">{{Encode(item.Kind)}}</p><h2>{{Encode(item.Title)}}</h2><p>{{Encode(item.Description)}}</p></div>
        </article>
        """;

    private static string SearchCard((string Title, string Summary, string Path, string Kind) item) =>
        $"<article class=\"card\"><p class=\"eyebrow\">{Encode(item.Kind)}</p><h2><a href=\"{Encode(item.Path)}\">{Encode(item.Title)}</a></h2><p>{Encode(item.Summary)}</p></article>";

    private static string Paragraphs(string body) =>
        string.Join(
            string.Empty,
            body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(paragraph =>
                    paragraph.Any(character => character is >= '\u0600' and <= '\u06ff')
                        ? $"<p class=\"arabic\" dir=\"rtl\" lang=\"ar\">{Encode(paragraph)}</p>"
                        : $"<p>{Encode(paragraph)}</p>"));

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string EscapeIcal(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace(";", @"\;", StringComparison.Ordinal)
            .Replace(",", @"\,", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", @"\n", StringComparison.Ordinal);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
