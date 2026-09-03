using System;
using System.Collections.Generic;

namespace TCFAnimation;

public enum DialogueKey
{
    Enter,
    Escape,
    P,
    Other,
}

public readonly record struct DialogueAction(
    bool Handled,
    bool Opened,
    bool Closed,
    bool Submitted,
    bool Hidden);

public sealed class DialogueModel
{
    public bool IsEditing { get; private set; }

    public bool IsBubbleVisible { get; private set; }

    public string BubbleText { get; private set; } = string.Empty;

    public bool SuppressCharacterInput => IsEditing;

    public DialogueAction HandleKey(
        DialogueKey key,
        bool echo,
        string draftText = "")
    {
        if (echo)
        {
            return default;
        }

        if (IsEditing)
        {
            if (key == DialogueKey.Enter)
            {
                string normalized = DialogueLayout.NormalizeText(draftText);
                IsEditing = false;
                if (normalized.Length == 0)
                {
                    return new DialogueAction(true, false, true, false, false);
                }

                BubbleText = normalized;
                IsBubbleVisible = true;
                return new DialogueAction(true, false, true, true, false);
            }

            if (key == DialogueKey.Escape)
            {
                IsEditing = false;
                return new DialogueAction(true, false, true, false, false);
            }

            return default;
        }

        if (key == DialogueKey.Enter)
        {
            IsEditing = true;
            return new DialogueAction(true, true, false, false, false);
        }

        if (key == DialogueKey.P)
        {
            bool wasVisible = IsBubbleVisible;
            IsBubbleVisible = false;
            return new DialogueAction(true, false, false, false, wasVisible);
        }

        return default;
    }

    public void SetPreviewText(string text)
    {
        BubbleText = DialogueLayout.NormalizeText(text);
        IsBubbleVisible = BubbleText.Length > 0;
    }

    public void OpenPreviewInput()
    {
        IsEditing = true;
    }
}

public readonly record struct DialogueSize(float Width, float Height);

public readonly record struct DialoguePoint(float X, float Y);

public readonly record struct DialogueRect(
    float X,
    float Y,
    float Width,
    float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;
}

public readonly record struct BubbleLayoutResult(
    DialogueRect Body,
    DialoguePoint TailTarget);

public static class DialogueLayout
{
    public const float ViewportWidth = 1920.0f;
    public const float ViewportHeight = 1080.0f;
    public const float SafeMargin = 24.0f;
    public const float MinimumBodyWidth = 220.0f;
    public const float MaximumBodyWidth = 680.0f;
    public const float MinimumBodyHeight = 86.0f;
    public const float MaximumBodyHeight = 238.0f;
    public const float HorizontalPadding = 34.0f;
    public const float VerticalPadding = 24.0f;
    public const float TailGap = 28.0f;
    public const int MaximumLines = 4;
    public const int MaximumInputCharacters = 500;

    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries));
    }

    public static string WrapText(
        string text,
        Func<string, float> measureWidth,
        float maximumTextWidth,
        int maximumLines)
    {
        ArgumentNullException.ThrowIfNull(measureWidth);
        if (maximumTextWidth <= 0.0f || !float.IsFinite(maximumTextWidth))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTextWidth));
        }

        if (maximumLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLines));
        }

        string normalized = NormalizeText(text);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        List<string> lines = [];
        string current = string.Empty;
        foreach (string word in normalized.Split(' '))
        {
            foreach (string segment in SplitOversizedWord(
                word,
                measureWidth,
                maximumTextWidth))
            {
                string candidate = current.Length == 0
                    ? segment
                    : $"{current} {segment}";
                if (
                    current.Length == 0
                    || measureWidth(candidate) <= maximumTextWidth
                )
                {
                    current = candidate;
                    continue;
                }

                lines.Add(current);
                current = segment;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        if (lines.Count > maximumLines)
        {
            lines = lines.GetRange(0, maximumLines);
            lines[^1] = FitEllipsis(
                lines[^1],
                measureWidth,
                maximumTextWidth);
        }

        return string.Join('\n', lines);
    }

    public static DialogueSize CalculateBodySize(DialogueSize measuredText)
    {
        float width = Math.Clamp(
            measuredText.Width + HorizontalPadding * 2.0f,
            MinimumBodyWidth,
            MaximumBodyWidth);
        float height = Math.Clamp(
            measuredText.Height + VerticalPadding * 2.0f,
            MinimumBodyHeight,
            MaximumBodyHeight);
        return new DialogueSize(width, height);
    }

    public static BubbleLayoutResult PlaceBubble(
        DialoguePoint headAnchor,
        DialogueSize bodySize)
    {
        float width = Math.Clamp(
            bodySize.Width,
            MinimumBodyWidth,
            MaximumBodyWidth);
        float height = Math.Clamp(
            bodySize.Height,
            MinimumBodyHeight,
            MaximumBodyHeight);
        float x = Math.Clamp(
            headAnchor.X - width / 2.0f,
            SafeMargin,
            ViewportWidth - SafeMargin - width);
        float idealY = headAnchor.Y - TailGap - height;
        float y = Math.Clamp(
            idealY,
            SafeMargin,
            ViewportHeight - SafeMargin - height);

        return new BubbleLayoutResult(
            new DialogueRect(x, y, width, height),
            headAnchor);
    }

    private static IEnumerable<string> SplitOversizedWord(
        string word,
        Func<string, float> measureWidth,
        float maximumWidth)
    {
        string segment = string.Empty;
        foreach (char character in word)
        {
            string candidate = segment + character;
            if (
                segment.Length > 0
                && measureWidth(candidate) > maximumWidth
            )
            {
                yield return segment;
                segment = character.ToString();
            }
            else
            {
                segment = candidate;
            }
        }

        if (segment.Length > 0)
        {
            yield return segment;
        }
    }

    private static string FitEllipsis(
        string line,
        Func<string, float> measureWidth,
        float maximumWidth)
    {
        const string ellipsis = "…";
        string fitted = line.TrimEnd();
        while (
            fitted.Length > 0
            && measureWidth(fitted + ellipsis) > maximumWidth
        )
        {
            fitted = fitted[..^1].TrimEnd();
        }

        return fitted + ellipsis;
    }
}
