namespace TCFAnimation;

public enum GlobalInputAction
{
    None,
    ToggleFullscreen,
    ExitFullscreen,
}

public enum GlobalInputKey
{
    F11,
    Enter,
    Escape,
    Other,
}

public enum GlobalInputPhase
{
    EarlyInput,
    UnhandledKeyInput,
}

public static class GlobalInputPolicy
{
    public static GlobalInputAction Resolve(
        GlobalInputPhase phase,
        GlobalInputKey key,
        bool pressed,
        bool echo,
        bool altPressed,
        bool dialogueEditing,
        bool fullscreen)
    {
        if (
            phase != GlobalInputPhase.EarlyInput
            || !pressed
            || echo
        )
        {
            return GlobalInputAction.None;
        }

        if (key == GlobalInputKey.F11)
        {
            return GlobalInputAction.ToggleFullscreen;
        }

        if (key == GlobalInputKey.Enter && altPressed)
        {
            return GlobalInputAction.ToggleFullscreen;
        }

        if (
            key == GlobalInputKey.Escape
            && !dialogueEditing
            && fullscreen
        )
        {
            return GlobalInputAction.ExitFullscreen;
        }

        return GlobalInputAction.None;
    }
}
