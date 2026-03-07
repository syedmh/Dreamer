using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace DayStartMCP.Tools;

[McpServerToolType]
public static class MeetingTools
{
    private static readonly string WorkspaceRoot = @"c:/Users/syedhu/work";

    // Project keyword mapping — priority order: index 0 = highest
    private static readonly (string Code, string Folder, string[] Keywords)[] ProjectMap =
    [
        ("SMI", "projects/SMI/", ["SMI", "Strong Machine Identity", "machine identity",
            "hardware-rooted identity", "AP-PKI", "AutoPilot PKI", "OS-SKU",
            "fleet readiness", "fleet monitoring", "bootstrapping", "claims enablement",
            "SDP alignment", "AppKI ACL", "SDP", "Service Deployment Framework"]),

        ("KG", "projects/KG/", ["KeyGuard", "KG Investigation", "key guard",
            "TPM attestation", "hardware-backed key", "hub-spoke", "KDA"]),

        ("certs", "projects/certs/", ["certificate", "cert rotation", "cert eviction",
            "secrets management", "CredSMART", "bowler", "MFC",
            "Machine Function Certificate", "Cosmic Certs"]),

        ("SAI", "projects/SAI/", ["SAI", "Substrate Application Identity",
            "application identity", "FIC", "Federated Identity Credentials",
            "Cosmic native cert", "SHIELD x CoreAuth"]),

        ("hpa", "projects/hpa/", ["HPA", "High Privilege Access",
            "high privilege", "privileged access"]),

        ("segmentation", "projects/segmentation/", ["segmentation",
            "network segmentation", "service segmentation", "global scopes"]),

        ("risk_register", "projects/risk_register/", ["risk register",
            "risk review", "risk tracking"]),
    ];

    // SMI workstream mapping
    private static readonly (string Slug, string Folder, string[] Keywords)[] SmiWorkstreams =
    [
        ("bootstrapping",    "ws-bootstrapping/",    ["bootstrap", "early bootstrapping", "early boot", "Torus"]),
        ("kg-enablement",    "ws-kg-enablement/",    ["KG enablement", "KeyGuard enablement", "KG enable", "KeyGuard rollout"]),
        ("fleet-readiness",  "ws-fleet-readiness/",  ["fleet readiness", "fleet ready", "OS-SKU"]),
        ("fleet-monitoring", "ws-fleet-monitoring/", ["fleet monitoring", "fleet monitor", "telemetry fleet"]),
        ("claims-enablement","ws-claims-enablement/",["claims enablement", "claims enable", "token claims"]),
        ("appki-acl",        "ws-appki-acl/",        ["AppKI", "ACL", "AppKI ACL"]),
        ("sdp-alignment",    "ws-sdp-alignment/",    ["SDP", "SDP alignment", "Service Deployment Framework", "SDP Alignment"]),
    ];

    // certs workstream mapping
    private static readonly (string Slug, string Folder, string[] Keywords)[] CertsWorkstreams =
    [
        ("kg", "ws-kg/", ["KeyGuard", "KG", "KDA"]),
    ];

    [McpServerTool, Description(
        "Classify a meeting into a project code and optional workstream using keyword matching. " +
        "Returns a JSON object with: project (string|null), workstream (string|null), " +
        "projectFolder (string|null), workstreamFolder (string|null).")]
    public static string ClassifyMeeting(
        [Description("Meeting title")] string title,
        [Description("Comma-separated list of attendee names (optional)")] string? attendees = null,
        [Description("Meeting description or agenda text (optional)")] string? description = null)
    {
        var text = string.Join(" ", new[] { title, attendees, description }
            .Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();

        string? projectCode = null;
        string? projectFolder = null;

        // Score each project — select the one with the most keyword hits;
        // ties broken by priority order (index order)
        int bestScore = 0;
        int bestIndex = -1;

        for (int i = 0; i < ProjectMap.Length; i++)
        {
            var (_, _, keywords) = ProjectMap[i];
            int score = keywords.Count(k => text.Contains(k.ToLowerInvariant()));
            if (score > bestScore || (score > 0 && bestIndex == -1))
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        string? workstreamSlug = null;
        string? workstreamFolder = null;

        if (bestIndex >= 0 && bestScore > 0)
        {
            var (code, folder, _) = ProjectMap[bestIndex];
            projectCode = code;
            projectFolder = folder;

            // Workstream sub-classification
            var wsMap = code == "SMI" ? SmiWorkstreams
                      : code == "certs" ? CertsWorkstreams
                      : [];

            foreach (var (slug, wsFolder, wsKeywords) in wsMap)
            {
                if (wsKeywords.Any(k => text.Contains(k.ToLowerInvariant())))
                {
                    workstreamSlug = slug;
                    workstreamFolder = wsFolder;
                    break;
                }
            }
        }

        var result = new
        {
            project = projectCode,
            workstream = workstreamSlug,
            projectFolder,
            workstreamFolder
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description(
        "Resolve the full storage path and file name for a meeting note. " +
        "Applies the schema rules: workstream path > project path > top-level meetings/. " +
        "Returns the absolute file path where the meeting note should be saved.")]
    public static string GetMeetingNotePath(
        [Description("Meeting title (used for file name sanitization)")] string title,
        [Description("Project code from ClassifyMeeting (optional)")] string? project = null,
        [Description("Workstream slug from ClassifyMeeting (optional)")] string? workstream = null,
        [Description("Meeting date in yyyy-MM-dd format (defaults to today)")] string? date = null)
    {
        var meetingDate = date is not null && DateTime.TryParse(date, out var d) ? d : DateTime.Today;
        var datePrefix = meetingDate.ToString("MMM-dd-yyyy").ToLowerInvariant();
        var sanitizedTitle = SanitizeTitle(title);
        var fileName = $"{datePrefix}-{sanitizedTitle}.md";

        string folder;

        if (!string.IsNullOrWhiteSpace(project) && !string.IsNullOrWhiteSpace(workstream))
        {
            // Find the project folder
            var projectEntry = Array.Find(ProjectMap, p => p.Code == project);
            var projectFolder = projectEntry != default ? projectEntry.Folder : $"projects/{project}/";

            // Find the workstream folder
            var wsMap = project == "SMI" ? SmiWorkstreams
                      : project == "certs" ? CertsWorkstreams
                      : [];
            var wsEntry = Array.Find(wsMap, w => w.Slug == workstream);
            var wsFolder = wsEntry != default ? wsEntry.Folder : $"ws-{workstream}/";

            folder = Path.Combine(WorkspaceRoot, projectFolder, wsFolder, "meetings/");
        }
        else if (!string.IsNullOrWhiteSpace(project))
        {
            var projectEntry = Array.Find(ProjectMap, p => p.Code == project);
            var projectFolder = projectEntry != default ? projectEntry.Folder : $"projects/{project}/";
            folder = Path.Combine(WorkspaceRoot, projectFolder, "meetings/");
        }
        else
        {
            folder = Path.Combine(WorkspaceRoot, "meetings/");
        }

        return Path.Combine(folder, fileName).Replace('\\', '/');
    }

    private static string SanitizeTitle(string title)
    {
        // Lowercase, replace non-alphanumeric with hyphens, collapse, trim
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

        return sanitized.Length > 60 ? sanitized[..60].TrimEnd('-') : sanitized;
    }
}
