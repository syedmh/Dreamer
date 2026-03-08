using System.Drawing;

namespace Pong;

public class Bullet
{
    public float X { get; set; }
    public float Y { get; set; }
    public float SpeedX { get; set; }
    public int Width { get; } = 6;
    public int Height { get; } = 3;
    public bool Active { get; set; }

    public RectangleF Bounds => new(X, Y - Height / 2f, Width, Height);

    public Bullet() { }

    public void Fire(float startX, float startY, float direction)
    {
        X = startX;
        Y = startY;
        SpeedX = direction * 10f;
        Active = true;
    }

    public void Update()
    {
        if (Active) X += SpeedX;
    }

    public void Deactivate() => Active = false;
}
