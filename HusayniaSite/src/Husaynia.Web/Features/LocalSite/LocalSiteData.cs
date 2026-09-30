using System.Collections.Concurrent;

namespace Husaynia.Web.Features.LocalSite;

public static class LocalDonationCategories
{
    public static IReadOnlyDictionary<string, string> All { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["general"] = "General",
            ["building-fund"] = "Building Fund",
            ["programs"] = "Programs",
        };

    public static bool IsSupported(string category) =>
        All.ContainsKey(category);
}

public sealed record LocalPage(
    string Slug,
    string Title,
    string Eyebrow,
    string Summary,
    string Body,
    string[] Tags);

public sealed record LocalEvent(
    string Slug,
    string Title,
    DateTimeOffset StartsAt,
    string Location,
    string Summary,
    string Category);

public sealed record LocalPrayer(
    string Name,
    string Time,
    string Note);

public sealed record LocalSocialItem(
    string Key,
    string Source,
    string Title,
    string Summary,
    DateOnly PublishedOn);

public sealed record LocalMediaItem(
    string Kind,
    string Title,
    string Description,
    string AssetPath);

public sealed record LocalAuditEntry(
    DateTimeOffset OccurredAt,
    string Action,
    string Target,
    string Outcome);

public sealed class LocalSiteStore(TimeProvider timeProvider)
{
    private readonly ConcurrentQueue<LocalAuditEntry> audit = new();
    private readonly ConcurrentDictionary<string, bool> published =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["community-update"] = true,
            ["volunteer-call"] = false,
        };
    private int formReceipt;

    public IReadOnlyList<LocalPage> Pages { get; } =
    [
        new(
            "overview",
            "About Husaynia",
            "Welcome",
            "A representative introduction to Husaynia Islamic Society of Seattle.",
            "This local fixture demonstrates a welcoming community page, service times, facilities, education, and charitable activity without copying unapproved production content.",
            ["community", "about"]),
        new(
            "build-husaynia",
            "Build Husaynia",
            "Community project",
            "A synthetic project update and call to participate.",
            "The local site shows how project milestones, progress, volunteer opportunities, and donation calls-to-action will be presented. Amounts and claims are intentionally illustrative.",
            ["construction", "community"]),
        new(
            "duas",
            "Duas and Rites",
            "Religious resources",
            "Representative religious-content navigation with Unicode and right-to-left text.",
            "اللَّهُمَّ صَلِّ عَلَىٰ مُحَمَّدٍ وَآلِ مُحَمَّدٍ\n\nTransliteration and English translation areas are displayed together so typography, reading order, and audio states can be tested locally.",
            ["dua", "religious"]),
        new(
            "dua-kumayl",
            "Dua Kumayl",
            "Thursday night",
            "A representative religious-content detail page.",
            "بِسْمِ اللَّهِ الرَّحْمَنِ الرَّحِيمِ\n\nThis fixture is deliberately abbreviated. Production text and attribution remain gated on the approved content snapshot.",
            ["dua", "thursday"]),
        new(
            "privacy-policy",
            "Privacy",
            "Your choices",
            "Local-mode privacy behavior.",
            "Local mode loads no analytics, advertising, social embeds, payment processors, or external media. Contact and pledge demonstrations are held only in process memory and disappear when the app stops.",
            ["privacy", "legal"]),
        new(
            "terms-and-conditions",
            "Terms",
            "Local demonstration",
            "Terms for the synthetic local fixture.",
            "This development-only site is not a production publication, donation processor, or authoritative schedule. It is intended solely for local engineering and acceptance testing.",
            ["terms", "legal"]),
    ];

    public IReadOnlyList<LocalEvent> Events { get; } =
    [
        new(
            "friday-prayers",
            "Friday Prayers",
            new DateTimeOffset(2026, 9, 4, 13, 0, 0, TimeSpan.FromHours(-7)),
            "Main prayer hall",
            "Representative weekly prayer gathering with doors opening thirty minutes early.",
            "Programs"),
        new(
            "community-service-day",
            "Community Service Day",
            new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.FromHours(-7)),
            "Community center",
            "A synthetic volunteer event used to exercise event details, categories, and calendar export.",
            "Community"),
        new(
            "dua-kumayl-program",
            "Dua Kumayl Program",
            new DateTimeOffset(2026, 9, 17, 19, 30, 0, TimeSpan.FromHours(-7)),
            "Main prayer hall",
            "Representative Thursday evening program.",
            "Religious"),
    ];

    public IReadOnlyList<LocalPrayer> Prayers { get; } =
    [
        new("Fajr", "5:12 AM", "Synthetic local fixture"),
        new("Sunrise", "6:38 AM", "Synthetic local fixture"),
        new("Dhuhr", "1:08 PM", "Synthetic local fixture"),
        new("Maghrib", "7:42 PM", "Synthetic local fixture"),
    ];

    public IReadOnlyList<LocalSocialItem> Social { get; } =
    [
        new(
            "community-update",
            "Facebook snapshot",
            "Arbaeen community program",
            "Persisted-snapshot presentation with the external embed intentionally disabled.",
            new DateOnly(2026, 8, 26)),
        new(
            "volunteer-call",
            "Instagram snapshot",
            "Volunteer appreciation",
            "Representative social content; no visitor data or third-party scripts are sent.",
            new DateOnly(2026, 8, 24)),
    ];

    public IReadOnlyList<LocalMediaItem> Media { get; } =
    [
        new(
            "Photo",
            "Community hall",
            "Synthetic vector placeholder with descriptive alternative text.",
            "/images/community-hall.svg"),
        new(
            "Photo",
            "Husaynia project",
            "Synthetic vector placeholder for construction progress.",
            "/images/project.svg"),
        new(
            "Audio",
            "Monday rites audio",
            "Audio delivery is represented as unavailable until approved media is imported.",
            string.Empty),
    ];

    public IReadOnlyDictionary<string, bool> PublicationState =>
        published.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LocalSocialItem> PublishedSocial =>
        Social.Where(item =>
                published.TryGetValue(item.Key, out var isPublished) &&
                isPublished)
            .ToArray();

    public IReadOnlyList<LocalAuditEntry> Audit =>
        audit.Reverse().Take(20).ToArray();

    public string RecordFormReceipt(string formKey)
    {
        var receipt = Interlocked.Increment(ref formReceipt);
        audit.Enqueue(new LocalAuditEntry(
            timeProvider.GetUtcNow(),
            "local.form.accepted",
            formKey,
            "synthetic-no-delivery"));
        return $"LOCAL-{receipt:0000}";
    }

    public bool TogglePublication(string key)
    {
        var next = published.AddOrUpdate(key, true, (_, current) => !current);
        audit.Enqueue(new LocalAuditEntry(
            timeProvider.GetUtcNow(),
            next ? "local.content.publish" : "local.content.unpublish",
            key,
            "in-memory"));
        return next;
    }
}
