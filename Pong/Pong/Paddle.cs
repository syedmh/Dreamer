using System.Drawing;

namespace Pong;

public class Paddle
{
    public float X { get; set; }
    public float Y { get; set; }
    public int Width { get; } = 10;
    public int Height { get; } = 100;
    public float Speed { get; set; } = 6f;

    public RectangleF Bounds => new(X, Y, Width, Height);

    public Paddle(float x, float y)
    {
        X = x;
        Y = y;
    }

    public void MoveUp() => Y -= Speed;
    public void MoveDown() => Y += Speed;
    public void MoveLeft() => X -= Speed;
    public void MoveRight() => X += Speed;

    public void Clamp(float minY, float maxY)
    {
        if (Y < minY) Y = minY;
        if (Y + Height > maxY) Y = maxY - Height;
    }

    public void ClampXY(float minX, float maxX, float minY, float maxY)
    {
        if (X < minX) X = minX;
        if (X + Width > maxX) X = maxX - Width;
        if (Y < minY) Y = minY;
        if (Y + Height > maxY) Y = maxY - Height;
    }
}
