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
    public static void PowerUpPickup() => PlayAsync(600, 60, 1, 1000);
    public static void Shoot() => PlayAsync(1200, 40);
    public static void Stun() => PlayAsync(150, 200);

    private static void PlayAsync(int frequency, int durationMs, int repeats = 1, int endFreq = 0)
    {
        Task.Run(() =>
        {
            try
            {
                if (endFreq > 0 && repeats == 1)
                {
                    // Ascending beep effect
                    int steps = 3;
                    int stepDur = durationMs / steps;
                    for (int i = 0; i < steps; i++)
                    {
                        int freq = frequency + (endFreq - frequency) * i / (steps - 1);
                        Console.Beep(freq, stepDur);
                    }
                    return;
                }
                for (int i = 0; i < repeats; i++)
                {
                    Console.Beep(frequency, durationMs);
                    if (repeats > 1) Thread.Sleep(80);
                }
            }
            catch { }
        });
    }
}
