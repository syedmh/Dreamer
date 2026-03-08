using System.Drawing;

namespace Pong;

public class Paddle
{
    public float X { get; set; }
    public float Y { get; set; }
    public int Width { get; } = 10;
    public int Height { get; } = 100;
    public float Speed { get; set; } = 6f;

    // Power-up state
    public bool HasGun { get; set; }
    public int Ammo { get; set; }

    // Stun state
    public bool IsStunned => StunFramesLeft > 0;
    public int StunFramesLeft { get; private set; }

    public RectangleF Bounds => new(X, Y, Width, Height);

    public Paddle(float x, float y)
    {
        X = x;
        Y = y;
    }

    public void MoveUp() { if (!IsStunned) Y -= Speed; }
    public void MoveDown() { if (!IsStunned) Y += Speed; }
    public void MoveLeft() { if (!IsStunned) X -= Speed; }
    public void MoveRight() { if (!IsStunned) X += Speed; }

    public void ApplyStun(int frames)
    {
        StunFramesLeft = frames;
    }

    public void UpdateStun()
    {
        if (StunFramesLeft > 0) StunFramesLeft--;
    }

    public void ResetPowerUps()
    {
        HasGun = false;
        Ammo = 0;
        StunFramesLeft = 0;
    }

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
