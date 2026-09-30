namespace TCFPreview.Core;

public sealed class PhotoNavigator
{
    private readonly IReadOnlyList<string> _photos;

    public PhotoNavigator(IReadOnlyList<string> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);

        if (photos.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Photo paths cannot be null or empty.", nameof(photos));
        }

        _photos = photos.ToArray();
        CurrentIndex = _photos.Count == 0 ? -1 : 0;
    }

    public string? CurrentPath => CurrentIndex >= 0 ? _photos[CurrentIndex] : null;

    public int CurrentIndex { get; private set; }

    public int Count => _photos.Count;

    public bool CanMovePrevious => CurrentIndex > 0;

    public bool CanMoveNext => CurrentIndex >= 0 && CurrentIndex < Count - 1;

    public bool TryMovePrevious(out string? path)
    {
        if (!CanMovePrevious)
        {
            path = CurrentPath;
            return false;
        }

        CurrentIndex--;
        path = CurrentPath;
        return true;
    }

    public bool TryMoveNext(out string? path)
    {
        if (!CanMoveNext)
        {
            path = CurrentPath;
            return false;
        }

        CurrentIndex++;
        path = CurrentPath;
        return true;
    }
}
