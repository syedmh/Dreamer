using Husaynia.BaselineCapture;

const string Sentinel = "--husaynia-promotion-crash-test";
const string RootPrefix = "husaynia-promotion-crash-";

if (args.Length != 6
    || !args[0].Equals(Sentinel, StringComparison.Ordinal)
    || !IsNonce(args[1])
    || !Enum.TryParse<PromotionFaultPoint>(
        args[5],
        ignoreCase: false,
        out var crashPoint))
{
    return 2;
}

string authorizedRoot;
string source;
string destination;
try
{
    authorizedRoot = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(args[2]));
    source = Path.GetFullPath(args[3]);
    destination = Path.GetFullPath(args[4]);
    var tempRoot = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(Path.GetTempPath()));
    var expectedRoot = Path.Combine(
        tempRoot,
        RootPrefix + args[1]);
    if (!authorizedRoot.Equals(
            expectedRoot,
            StringComparison.OrdinalIgnoreCase)
        || !Directory.Exists(authorizedRoot)
        || !IsStrictDescendant(authorizedRoot, source)
        || !IsStrictDescendant(authorizedRoot, destination)
        || source.Equals(
            destination,
            StringComparison.OrdinalIgnoreCase))
    {
        return 2;
    }

    CaptureIO.EnsureNoReparsePath(authorizedRoot);
    CaptureIO.EnsureNoReparsePath(source);
    CaptureIO.EnsureNoReparsePath(destination);
}
catch (Exception exception) when (
    exception is ArgumentException
        or IOException
        or UnauthorizedAccessException
        or CaptureSafetyException)
{
    return 2;
}

return await new BaselinePromotionService(
        faultInjector: point =>
        {
            if (point == crashPoint)
            {
                Environment.Exit(97);
            }
        })
    .PromoteAsync(
        source,
        destination,
        CancellationToken.None);

static bool IsNonce(string value) =>
    value.Length == 32
    && value.All(character =>
        character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

static bool IsStrictDescendant(
    string authorizedRoot,
    string path)
{
    var relative = Path.GetRelativePath(
        authorizedRoot,
        path);
    return !relative.Equals(".", StringComparison.Ordinal)
        && !Path.IsPathRooted(relative)
        && !relative.Equals("..", StringComparison.Ordinal)
        && !relative.StartsWith(
            $"..{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
        && !relative.StartsWith(
            $"..{Path.AltDirectorySeparatorChar}",
            StringComparison.Ordinal);
}
