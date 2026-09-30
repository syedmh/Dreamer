using System.Text;

namespace Husaynia.Application.Contracts;

public sealed record RouteManifestContract(IReadOnlyList<RouteManifestEntry> Routes);

public sealed record RouteManifestEntry(
    string RouteId,
    string LegacyPath,
    string CanonicalPath,
    int ExpectedStatus,
    string? RedirectTarget);

public sealed record RouteManifestValidationError(string Code, string Message, string? RouteId = null);

public interface IRouteManifestValidator
{
    IReadOnlyList<RouteManifestValidationError> Validate(RouteManifestContract manifest);
}

public sealed class RouteManifestValidator : IRouteManifestValidator
{
    private static readonly HashSet<int> RedirectStatuses = [301, 308];

    public IReadOnlyList<RouteManifestValidationError> Validate(RouteManifestContract manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var errors = new List<RouteManifestValidationError>();
        AddDuplicateErrors(manifest.Routes, errors);
        AddNormalizationErrors(manifest.Routes, errors);
        AddRedirectGraphErrors(manifest.Routes, errors);
        return errors;
    }

    private static void AddDuplicateErrors(
        IReadOnlyList<RouteManifestEntry> routes,
        List<RouteManifestValidationError> errors)
    {
        AddDuplicates(
            routes,
            route => route.RouteId,
            "duplicate-route-id",
            "Route ID",
            errors);
        AddDuplicates(
            routes,
            route => route.CanonicalPath,
            "duplicate-canonical-path",
            "Canonical path",
            errors);
    }

    private static void AddDuplicates(
        IEnumerable<RouteManifestEntry> routes,
        Func<RouteManifestEntry, string> keySelector,
        string code,
        string label,
        List<RouteManifestValidationError> errors)
    {
        foreach (var duplicate in routes
            .GroupBy(keySelector, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any()))
        {
            errors.Add(new RouteManifestValidationError(
                code,
                $"{label} '{duplicate.Key}' must be unique."));
        }
    }

    private static void AddNormalizationErrors(
        IEnumerable<RouteManifestEntry> routes,
        List<RouteManifestValidationError> errors)
    {
        foreach (var route in routes)
        {
            AddPathNormalizationError(route.RouteId, "legacyPath", route.LegacyPath, errors);
            AddPathNormalizationError(route.RouteId, "canonicalPath", route.CanonicalPath, errors);
            if (route.RedirectTarget is not null)
            {
                AddPathNormalizationError(route.RouteId, "redirectTarget", route.RedirectTarget, errors);
            }
        }
    }

    private static void AddPathNormalizationError(
        string routeId,
        string field,
        string path,
        List<RouteManifestValidationError> errors)
    {
        if (!path.IsNormalized(NormalizationForm.FormC))
        {
            errors.Add(new RouteManifestValidationError(
                "path-not-nfc",
                $"{field} must be Unicode NFC-normalized.",
                routeId));
        }
    }

    private static void AddRedirectGraphErrors(
        IReadOnlyList<RouteManifestEntry> routes,
        List<RouteManifestValidationError> errors)
    {
        var redirectsBySource = routes
            .Where(IsRedirect)
            .GroupBy(route => route.LegacyPath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var route in redirectsBySource.Values)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { route.LegacyPath };
            var target = route.RedirectTarget!;

            if (!redirectsBySource.TryGetValue(target, out var next))
            {
                continue;
            }

            while (true)
            {
                if (!visited.Add(target))
                {
                    errors.Add(new RouteManifestValidationError(
                        "redirect-cycle",
                        $"Redirect from '{route.LegacyPath}' participates in a cycle.",
                        route.RouteId));
                    break;
                }

                if (!redirectsBySource.TryGetValue(next.RedirectTarget!, out var following))
                {
                    errors.Add(new RouteManifestValidationError(
                        "redirect-chain",
                        $"Redirect from '{route.LegacyPath}' must resolve in one hop.",
                        route.RouteId));
                    break;
                }

                target = next.RedirectTarget!;
                next = following;
            }
        }
    }

    private static bool IsRedirect(RouteManifestEntry route) =>
        RedirectStatuses.Contains(route.ExpectedStatus) && route.RedirectTarget is not null;
}
