using System.Text;
using Husaynia.Application.Contracts;

namespace Husaynia.ContractTests;

public sealed class RouteManifestValidatorTests
{
    private readonly RouteManifestValidator validator = new();

    [Fact]
    public void ValidManifestPassesSemanticValidation()
    {
        var manifest = Manifest(
            Route("home", "/", "/"),
            Route("legacy", "/old/", "/old/", 301, "/new/"),
            Route("new", "/new/", "/new/"));

        Assert.Empty(validator.Validate(manifest));
    }

    [Fact]
    public void DuplicateRouteIdsAreRejected()
    {
        var errors = validator.Validate(Manifest(Route("duplicate", "/a/", "/a/"), Route("duplicate", "/b/", "/b/")));

        Assert.Contains(errors, error => error.Code == "duplicate-route-id");
    }

    [Fact]
    public void DuplicateCanonicalPathsAreRejected()
    {
        var errors = validator.Validate(Manifest(Route("a", "/a/", "/same/"), Route("b", "/b/", "/same/")));

        Assert.Contains(errors, error => error.Code == "duplicate-canonical-path");
    }

    [Fact]
    public void RedirectChainsAreRejected()
    {
        var errors = validator.Validate(Manifest(
            Route("a", "/a/", "/b/", 301, "/b/"),
            Route("b", "/b/", "/c/", 308, "/c/"),
            Route("c", "/c/", "/c/")));

        Assert.Contains(errors, error => error.Code == "redirect-chain");
    }

    [Fact]
    public void RedirectCyclesAreRejected()
    {
        var errors = validator.Validate(Manifest(
            Route("a", "/a/", "/b/", 301, "/b/"),
            Route("b", "/b/", "/a/", 308, "/a/")));

        Assert.Contains(errors, error => error.Code == "redirect-cycle");
    }

    [Fact]
    public void NonNfcPathsAreRejected()
    {
        var decomposedPath = $"/{new string(['e', '\u0301']).Normalize(NormalizationForm.FormD)}/";
        Assert.False(decomposedPath.IsNormalized(NormalizationForm.FormC));

        var errors = validator.Validate(Manifest(Route("unicode", decomposedPath, decomposedPath)));

        Assert.Contains(errors, error => error.Code == "path-not-nfc");
    }

    private static RouteManifestContract Manifest(params RouteManifestEntry[] routes) => new(routes);

    private static RouteManifestEntry Route(
        string routeId,
        string legacyPath,
        string canonicalPath,
        int expectedStatus = 200,
        string? redirectTarget = null) =>
        new(routeId, legacyPath, canonicalPath, expectedStatus, redirectTarget);
}
