namespace Pong;

public enum AiDifficulty { Easy, Medium, Hard }

public class AiController
{
    private readonly Random _rng = new();
    private float _offset;
    private int _offsetTimer;

    public AiDifficulty Difficulty { get; set; } = AiDifficulty.Medium;
    public bool WantsToShoot { get; private set; }

    public void Update(Paddle paddle, Ball ball, int gameWidth, int gameHeight,
        PowerUp? powerUp = null, Paddle? opponent = null)
    {
        WantsToShoot = false;
        float paddleCenter = paddle.Y + paddle.Height / 2f;
        float ballCenter = ball.Y + ball.Height / 2f;
        bool ballApproaching = ball.SpeedX > 0;
        float targetY;
        float moveSpeed;

        // --- Power-up seeking ---
        // If a power-up is on our side and we don't have a gun, detour to grab it
        bool seekingPowerUp = false;
        if (powerUp != null && powerUp.Active && !paddle.HasGun)
        {
            float distToPowerUp = MathF.Abs(paddleCenter - powerUp.Y) +
                                  MathF.Abs(paddle.X + paddle.Width / 2f - powerUp.X);
            // Only detour if power-up is reachable and ball isn't immediately threatening
            if (distToPowerUp < 250 || !ballApproaching || ball.X < gameWidth * 0.3f)
            {
                seekingPowerUp = true;
            }
        }

        // --- Shooting logic ---
        if (paddle.HasGun && paddle.Ammo > 0 && opponent != null && !paddle.IsStunned)
        {
            switch (Difficulty)
            {
                case AiDifficulty.Easy:
                    // Fire randomly ~2% chance per frame
                    if (_rng.Next(50) == 0) WantsToShoot = true;
                    break;
                case AiDifficulty.Medium:
                    // Fire when opponent is far from center (harder to recover)
                    if (MathF.Abs(opponent.Y + opponent.Height / 2f - gameHeight / 2f) > 120)
                        WantsToShoot = true;
                    break;
                case AiDifficulty.Hard:
                    // Fire when ball is approaching and opponent is committed to a direction
                    if (ballApproaching && ball.X > gameWidth * 0.4f &&
                        MathF.Abs(opponent.Y + opponent.Height / 2f - gameHeight / 2f) > 80)
                        WantsToShoot = true;
                    break;
            }
        }

        // --- Horizontal movement ---
        float halfCourt = gameWidth / 2f;
        float homeX = gameWidth - paddle.Width;
        float targetX;

        if (seekingPowerUp && powerUp != null)
        {
            targetX = powerUp.X;
        }
        else if (ballApproaching && ball.X > halfCourt)
            targetX = Math.Max(halfCourt + 80, ball.X - 60);
        else if (ballApproaching)
            targetX = halfCourt + 100;
        else
            targetX = homeX;

        targetX = Math.Clamp(targetX, halfCourt + 20, gameWidth - paddle.Width);

        float xDiff = targetX - paddle.X;
        float xSpeed = paddle.Speed * 0.7f;
        if (MathF.Abs(xDiff) < xSpeed)
            paddle.X = targetX;
        else if (xDiff < 0)
            paddle.X -= xSpeed;
        else
            paddle.X += xSpeed;

        switch (Difficulty)
        {
            case AiDifficulty.Easy:
                _offsetTimer--;
                if (_offsetTimer <= 0)
                {
                    _offset = (_rng.NextSingle() - 0.5f) * 50f;
                    _offsetTimer = 40 + _rng.Next(20);
                }

                if (ballApproaching)
                {
                    targetY = ballCenter + _offset;
                    moveSpeed = paddle.Speed * 0.6f;
                }
                else
                {
                    targetY = gameHeight / 2f;
                    moveSpeed = paddle.Speed * 0.3f;
                }
                break;

            case AiDifficulty.Medium:
                _offsetTimer--;
                if (_offsetTimer <= 0)
                {
                    _offset = (_rng.NextSingle() - 0.5f) * 20f;
                    _offsetTimer = 30 + _rng.Next(30);
                }

                if (ballApproaching)
                {
                    targetY = PredictBallY(ball, paddle.X, gameHeight) + _offset;
                    moveSpeed = paddle.Speed * 0.85f;
                }
                else
                {
                    targetY = gameHeight / 2f;
                    moveSpeed = paddle.Speed * 0.4f;
                }
                break;

            case AiDifficulty.Hard:
                _offsetTimer--;
                if (_offsetTimer <= 0)
                {
                    _offset = (_rng.NextSingle() - 0.5f) * 8f;
                    _offsetTimer = 60 + _rng.Next(30);
                }

                if (ballApproaching)
                {
                    targetY = PredictBallY(ball, paddle.X, gameHeight) + _offset;
                    moveSpeed = paddle.Speed;
                }
                else
                {
                    targetY = gameHeight / 2f + (ballCenter - gameHeight / 2f) * 0.3f;
                    moveSpeed = paddle.Speed * 0.6f;
                }
                break;

            default:
                return;
        }

        // Override target Y if seeking a power-up and ball isn't urgent
        if (seekingPowerUp && powerUp != null && (!ballApproaching || ball.X < gameWidth * 0.5f))
        {
            targetY = powerUp.Y;
            moveSpeed = paddle.Speed * 0.9f;
        }

        // Move paddle toward target Y
        float diff = targetY - paddleCenter;
        if (MathF.Abs(diff) < moveSpeed)
            paddle.Y = targetY - paddle.Height / 2f;
        else if (diff < 0)
            paddle.Y -= moveSpeed;
        else
            paddle.Y += moveSpeed;

        paddle.ClampXY(gameWidth / 2 + 20, gameWidth, 0, gameHeight);
    }

    private static float PredictBallY(Ball ball, float paddleX, int gameHeight)
    {
        float simX = ball.X + ball.Width / 2f;
        float simY = ball.Y + ball.Height / 2f;
        float dx = ball.SpeedX;
        float dy = ball.SpeedY;

        if (dx <= 0) return simY;

        // Step the ball forward to the paddle's X position
        float timeToReach = (paddleX - simX) / dx;
        if (timeToReach < 0) return simY;

        simY += dy * timeToReach;

        // Reflect off top/bottom walls
        // Normalize simY into the [0, gameHeight] range with bouncing
        float range = gameHeight;
        if (simY < 0) simY = -simY;
        int bounces = (int)(simY / range);
        simY = simY % range;
        if (bounces % 2 == 1) simY = range - simY;

        return simY;
    }
}
