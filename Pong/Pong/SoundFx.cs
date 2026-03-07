namespace Pong;

/// <summary>
/// Plays simple beep sounds on a background thread to avoid blocking the game loop.
/// </summary>
public static class SoundFx
{
    public static void PaddleHit() => PlayAsync(800, 50);
    public static void WallBounce() => PlayAsync(400, 50);
    public static void Score() => PlayAsync(200, 200);
    public static void Win() => PlayAsync(1000, 100, 3);

    private static void PlayAsync(int frequency, int durationMs, int repeats = 1)
    {
        Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < repeats; i++)
                {
                    Console.Beep(frequency, durationMs);
                    if (repeats > 1) Thread.Sleep(80);
                }
            }
            catch { /* ignore if beep unavailable */ }
        });
    }
}
