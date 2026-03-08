using System.Drawing;

namespace Pong;

public enum CourtSide { Left, Right }

public class PowerUp
{
    public float X { get; set; }
    public float Y { get; set; }
    public int Size { get; } = 16;
    public bool Active { get; set; }
    public CourtSide Side { get; set; }
    public int AnimFrame { get; set; }

    public RectangleF Bounds => new(X - Size / 2f, Y - Size / 2f, Size, Size);

    public PowerUp() { }

    public void Spawn(float x, float y, CourtSide side)
    {
        X = x;
        Y = y;
        Side = side;
        Active = true;
        AnimFrame = 0;
    }

    public void Deactivate() => Active = false;
}
