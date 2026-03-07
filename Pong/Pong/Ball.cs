using System.Drawing;

namespace Pong;

public class Ball
{
    public float X { get; set; }
    public float Y { get; set; }
    public float SpeedX { get; set; }
    public float SpeedY { get; set; }
    public int Width { get; } = 10;
    public int Height { get; } = 10;

    public RectangleF Bounds => new(X, Y, Width, Height);

    public Ball(float x, float y)
    {
        X = x;
        Y = y;
    }

    public void Reset(float x, float y)
    {
        X = x;
        Y = y;
        SpeedX = 0;
        SpeedY = 0;
    }
}
