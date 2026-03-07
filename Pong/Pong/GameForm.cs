using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Pong;

public class GameForm : Form
{
    private const int GameWidth = 800;
    private const int GameHeight = 600;
    private const int TargetFps = 60;
    private const float InitialBallSpeed = 5f;
    private const float SpeedIncreasePercent = 0.05f;
    private const int LaunchDelayMs = 1000;
    private const float MaxBounceAngle = 75f * MathF.PI / 180f;
    private const int WinningScore = 11;
    private const int TrailLength = 8;
    private const int ShakeDurationFrames = 4;
    private const int ShakeIntensity = 4;

    private readonly Panel _gamePanel;
    private readonly System.Windows.Forms.Timer _gameTimer;
    private BufferedGraphicsContext _bufContext = null!;
    private BufferedGraphics _bufGraphics = null!;

    private readonly Ball _ball;
    private readonly Paddle _leftPaddle;
    private readonly Paddle _rightPaddle;

    private readonly HashSet<Keys> _pressedKeys = new();
    private readonly HashSet<Keys> _justPressed = new();
    private readonly Random _rng = new();
    private readonly AiController _ai = new();

    // Trail history
    private readonly Queue<PointF> _ballTrail = new();
    private readonly Queue<float> _leftPaddleTrail = new();
    private readonly Queue<float> _rightPaddleTrail = new();

    // Background particles
    private readonly PointF[] _stars;
    private readonly float[] _starSpeeds;
    private readonly int[] _starBrightness;
    private int _bgPulseFrame;

    // Game state
    private bool _modeSelected;
    private bool _singlePlayer;
    private bool _difficultySelected;
    private bool _gameStarted;
    private bool _ballLaunched;
    private bool _matchOver;
    private bool _paused;
    private int _launchTimer;
    private float _currentBallSpeed;
    private int _scoreLeft;
    private int _scoreRight;

    // Screen shake
    private int _shakeFrames;

    // FPS counter
    private bool _showFps;
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private int _frameCount;
    private float _currentFps;

    public GameForm()
    {
        Text = "Pong";
        ClientSize = new Size(GameWidth, GameHeight);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;

        // Enable all double-buffer styles to eliminate flicker
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer, true);

        // Manual buffered graphics for best performance
        _bufContext = BufferedGraphicsManager.Current;
        _bufContext.MaximumBuffer = new Size(GameWidth + 1, GameHeight + 1);
        _bufGraphics = _bufContext.Allocate(CreateGraphics(),
            new Rectangle(0, 0, GameWidth, GameHeight));

        _ball = new Ball(
            (GameWidth - 10) / 2f,
            (GameHeight - 10) / 2f
        );

        _leftPaddle = new Paddle(0, (GameHeight - 100) / 2f);
        _rightPaddle = new Paddle(GameWidth - 10, (GameHeight - 100) / 2f);

        _currentBallSpeed = InitialBallSpeed;

        // Init starfield
        const int starCount = 80;
        _stars = new PointF[starCount];
        _starSpeeds = new float[starCount];
        _starBrightness = new int[starCount];
        for (int i = 0; i < starCount; i++)
        {
            _stars[i] = new PointF(_rng.Next(GameWidth), _rng.Next(GameHeight));
            _starSpeeds[i] = 0.1f + _rng.NextSingle() * 0.5f;
            _starBrightness[i] = 30 + _rng.Next(50);
        }

        _gameTimer = new System.Windows.Forms.Timer { Interval = 1000 / TargetFps };
        _gameTimer.Tick += (_, _) =>
        {
            Update();
            Draw();
            _justPressed.Clear();
        };
        _gameTimer.Start();

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        KeyPreview = true;
    }

    // Suppress background erase to prevent flicker
    protected override void OnPaintBackground(PaintEventArgs e) { }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_pressedKeys.Contains(e.KeyCode))
            _justPressed.Add(e.KeyCode);
        _pressedKeys.Add(e.KeyCode);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        _pressedKeys.Remove(e.KeyCode);
    }

    private void LaunchBall()
    {
        float angle = (_rng.NextSingle() - 0.5f) * MathF.PI / 2f;
        float direction = _rng.Next(2) == 0 ? -1f : 1f;
        _ball.SpeedX = MathF.Cos(angle) * _currentBallSpeed * direction;
        _ball.SpeedY = MathF.Sin(angle) * _currentBallSpeed;
        _ballLaunched = true;
        _ballTrail.Clear();
    }

    private void ResetBall()
    {
        _ball.Reset(
            (GameWidth - _ball.Width) / 2f,
            (GameHeight - _ball.Height) / 2f
        );
        _currentBallSpeed = InitialBallSpeed;
        _ballLaunched = false;
        _launchTimer = LaunchDelayMs / (1000 / TargetFps);
        _ballTrail.Clear();
    }

    private void TriggerShake() => _shakeFrames = ShakeDurationFrames;

    private new void Update()
    {
        // FPS tracking
        _frameCount++;
        if (_fpsStopwatch.ElapsedMilliseconds >= 500)
        {
            _currentFps = _frameCount / ((float)_fpsStopwatch.ElapsedMilliseconds / 1000f);
            _frameCount = 0;
            _fpsStopwatch.Restart();
        }

        if (_justPressed.Contains(Keys.F1))
            _showFps = !_showFps;

        // Mode selection screen
        if (!_modeSelected)
        {
            if (_justPressed.Contains(Keys.D1) || _justPressed.Contains(Keys.NumPad1))
            { _singlePlayer = true; _modeSelected = true; }
            else if (_justPressed.Contains(Keys.D2) || _justPressed.Contains(Keys.NumPad2))
            { _singlePlayer = false; _modeSelected = true; _difficultySelected = true; }
            return;
        }

        // Difficulty selection
        if (_singlePlayer && !_difficultySelected)
        {
            if (_justPressed.Contains(Keys.E))
            { _ai.Difficulty = AiDifficulty.Easy; _difficultySelected = true; }
            else if (_justPressed.Contains(Keys.M))
            { _ai.Difficulty = AiDifficulty.Medium; _difficultySelected = true; }
            else if (_justPressed.Contains(Keys.H))
            { _ai.Difficulty = AiDifficulty.Hard; _difficultySelected = true; }
            return;
        }

        // Pause toggle
        if (_justPressed.Contains(Keys.Escape) && _gameStarted && !_matchOver)
            _paused = !_paused;
        if (_paused) return;

        // Left paddle: W/S/A/D
        if (_pressedKeys.Contains(Keys.W)) _leftPaddle.MoveUp();
        if (_pressedKeys.Contains(Keys.S)) _leftPaddle.MoveDown();
        if (_pressedKeys.Contains(Keys.A)) _leftPaddle.MoveLeft();
        if (_pressedKeys.Contains(Keys.D)) _leftPaddle.MoveRight();

        // Right paddle: player or AI
        if (_singlePlayer)
            _ai.Update(_rightPaddle, _ball, GameWidth, GameHeight);
        else
        {
            if (_pressedKeys.Contains(Keys.Up)) _rightPaddle.MoveUp();
            if (_pressedKeys.Contains(Keys.Down)) _rightPaddle.MoveDown();
            if (_pressedKeys.Contains(Keys.Left)) _rightPaddle.MoveLeft();
            if (_pressedKeys.Contains(Keys.Right)) _rightPaddle.MoveRight();
        }

        // Clamp each paddle to their half of the court
        _leftPaddle.ClampXY(0, GameWidth / 2 - 20, 0, GameHeight);
        _rightPaddle.ClampXY(GameWidth / 2 + 20, GameWidth, 0, GameHeight);

        // Record paddle trails
        _leftPaddleTrail.Enqueue(_leftPaddle.Y);
        _rightPaddleTrail.Enqueue(_rightPaddle.Y);
        while (_leftPaddleTrail.Count > TrailLength) _leftPaddleTrail.Dequeue();
        while (_rightPaddleTrail.Count > TrailLength) _rightPaddleTrail.Dequeue();

        // First start
        if (!_gameStarted && _pressedKeys.Contains(Keys.Space))
        {
            _gameStarted = true;
            _launchTimer = LaunchDelayMs / (1000 / TargetFps);
        }
        if (!_gameStarted) return;

        // Restart match after win
        if (_matchOver)
        {
            if (_pressedKeys.Contains(Keys.Enter)) RestartMatch();
            return;
        }

        // Shake countdown
        if (_shakeFrames > 0) _shakeFrames--;

        // Launch delay
        if (!_ballLaunched)
        {
            _launchTimer--;
            if (_launchTimer <= 0) LaunchBall();
            return;
        }

        // Record ball trail
        _ballTrail.Enqueue(new PointF(_ball.X, _ball.Y));
        while (_ballTrail.Count > TrailLength) _ballTrail.Dequeue();

        // Move ball
        _ball.X += _ball.SpeedX;
        _ball.Y += _ball.SpeedY;

        // Wall bounce
        if (_ball.Y <= 0)
        { _ball.Y = 0; _ball.SpeedY = -_ball.SpeedY; SoundFx.WallBounce(); }
        else if (_ball.Y + _ball.Height >= GameHeight)
        { _ball.Y = GameHeight - _ball.Height; _ball.SpeedY = -_ball.SpeedY; SoundFx.WallBounce(); }

        // Left paddle collision
        if (_ball.Bounds.IntersectsWith(_leftPaddle.Bounds) && _ball.SpeedX < 0)
        { _ball.X = _leftPaddle.X + _leftPaddle.Width; BounceOffPaddle(_leftPaddle, 1f); SoundFx.PaddleHit(); }

        // Right paddle collision
        if (_ball.Bounds.IntersectsWith(_rightPaddle.Bounds) && _ball.SpeedX > 0)
        { _ball.X = _rightPaddle.X - _ball.Width; BounceOffPaddle(_rightPaddle, -1f); SoundFx.PaddleHit(); }

        // Scoring
        if (_ball.X + _ball.Width < 0)
        {
            _scoreRight++; TriggerShake();
            if (_scoreRight >= WinningScore) { _matchOver = true; SoundFx.Win(); }
            else { SoundFx.Score(); ResetBall(); }
        }
        else if (_ball.X > GameWidth)
        {
            _scoreLeft++; TriggerShake();
            if (_scoreLeft >= WinningScore) { _matchOver = true; SoundFx.Win(); }
            else { SoundFx.Score(); ResetBall(); }
        }
    }

    private void BounceOffPaddle(Paddle paddle, float directionX)
    {
        float paddleCenter = paddle.Y + paddle.Height / 2f;
        float ballCenter = _ball.Y + _ball.Height / 2f;
        float relativeHit = (ballCenter - paddleCenter) / (paddle.Height / 2f);
        relativeHit = Math.Clamp(relativeHit, -1f, 1f);

        float bounceAngle = relativeHit * MaxBounceAngle;

        _currentBallSpeed *= 1f + SpeedIncreasePercent;
        _ball.SpeedX = MathF.Cos(bounceAngle) * _currentBallSpeed * directionX;
        _ball.SpeedY = MathF.Sin(bounceAngle) * _currentBallSpeed;
    }

    private void RestartMatch()
    {
        _scoreLeft = 0;
        _scoreRight = 0;
        _matchOver = false;
        _gameStarted = false;
        _modeSelected = false;
        _difficultySelected = false;
        _paused = false;
        _leftPaddle.X = 0;
        _leftPaddle.Y = (GameHeight - _leftPaddle.Height) / 2f;
        _rightPaddle.X = GameWidth - _rightPaddle.Width;
        _rightPaddle.Y = (GameHeight - _rightPaddle.Height) / 2f;
        _ballTrail.Clear();
        _leftPaddleTrail.Clear();
        _rightPaddleTrail.Clear();
        ResetBall();
    }

    private void Draw()
    {
        var g = _bufGraphics.Graphics;
        g.Clear(Color.FromArgb(5, 5, 15));
        DrawBackground(g);
        RenderScene(g);
        _bufGraphics.Render();
    }

    private void DrawBackground(Graphics g)
    {
        _bgPulseFrame++;

        // Subtle radial gradient vignette
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(-100, -100, GameWidth + 200, GameHeight + 200);
            using var vignette = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(20, 15, 25, 50),
                SurroundColors = new[] { Color.FromArgb(180, 0, 0, 0) }
            };
            g.FillRectangle(vignette, 0, 0, GameWidth, GameHeight);
        }

        // Animated starfield
        for (int i = 0; i < _stars.Length; i++)
        {
            _stars[i].Y += _starSpeeds[i];
            if (_stars[i].Y > GameHeight)
            {
                _stars[i].Y = 0;
                _stars[i].X = _rng.Next(GameWidth);
            }

            // Twinkle effect
            float twinkle = MathF.Sin((_bgPulseFrame + i * 37) * 0.05f);
            int alpha = (int)(_starBrightness[i] + twinkle * 20);
            alpha = Math.Clamp(alpha, 10, 90);
            float size = _starSpeeds[i] > 0.35f ? 2f : 1f;

            using var brush = new SolidBrush(Color.FromArgb(alpha, 180, 200, 255));
            g.FillRectangle(brush, _stars[i].X, _stars[i].Y, size, size);
        }

        // Subtle glow behind the ball area during gameplay
        if (_ballLaunched)
        {
            int pulse = (int)(15 + 8 * MathF.Sin(_bgPulseFrame * 0.03f));
            using var glow = new SolidBrush(Color.FromArgb(pulse, 40, 80, 160));
            g.FillEllipse(glow,
                _ball.X - 40, _ball.Y - 40,
                _ball.Width + 80, _ball.Height + 80);
        }
    }

    private void RenderScene(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Screen shake offset
        float shakeX = 0, shakeY = 0;
        if (_shakeFrames > 0)
        {
            shakeX = (_rng.NextSingle() - 0.5f) * 2 * ShakeIntensity;
            shakeY = (_rng.NextSingle() - 0.5f) * 2 * ShakeIntensity;
            g.TranslateTransform(shakeX, shakeY);
        }

        // Center line
        using var dashPen = new Pen(Color.FromArgb(60, 255, 255, 255), 2)
            { DashStyle = DashStyle.Dash };
        g.DrawLine(dashPen, GameWidth / 2, 0, GameWidth / 2, GameHeight);

        // Court boundary markers (thin lines showing paddle zones)
        using var zonePen = new Pen(Color.FromArgb(25, 255, 255, 255), 1)
            { DashStyle = DashStyle.Dot };
        g.DrawLine(zonePen, GameWidth / 2 - 20, 0, GameWidth / 2 - 20, GameHeight);
        g.DrawLine(zonePen, GameWidth / 2 + 20, 0, GameWidth / 2 + 20, GameHeight);

        // Ball trail
        int idx = 0;
        foreach (var pos in _ballTrail)
        {
            int alpha = (int)(20 + 80 * ((float)idx / TrailLength));
            float size = _ball.Width * (0.5f + 0.5f * ((float)idx / TrailLength));
            using var trailBrush = new SolidBrush(Color.FromArgb(alpha, 100, 180, 255));
            g.FillEllipse(trailBrush,
                pos.X + (_ball.Width - size) / 2,
                pos.Y + (_ball.Height - size) / 2, size, size);
            idx++;
        }

        // Ball glow + ball
        using (var glowBrush = new SolidBrush(Color.FromArgb(40, 100, 180, 255)))
            g.FillEllipse(glowBrush, _ball.X - 3, _ball.Y - 3,
                _ball.Width + 6, _ball.Height + 6);
        g.FillEllipse(Brushes.White, _ball.Bounds);

        // Paddle trails
        DrawPaddleTrail(g, _leftPaddle, _leftPaddleTrail);
        DrawPaddleTrail(g, _rightPaddle, _rightPaddleTrail);

        // Paddle glow + paddles
        DrawPaddleGlow(g, _leftPaddle);
        DrawPaddleGlow(g, _rightPaddle);
        g.FillRectangle(Brushes.White, _leftPaddle.Bounds);
        g.FillRectangle(Brushes.White, _rightPaddle.Bounds);

        // Scores
        using var scoreFont = new Font("Consolas", 36, FontStyle.Bold);
        var leftScore = _scoreLeft.ToString();
        var rightScore = _scoreRight.ToString();
        var leftSize = g.MeasureString(leftScore, scoreFont);
        g.DrawString(leftScore, scoreFont, Brushes.White,
            GameWidth / 2 - leftSize.Width - 30, 20);
        g.DrawString(rightScore, scoreFont, Brushes.White,
            GameWidth / 2 + 30, 20);

        // Reset shake
        if (_shakeFrames > 0) g.TranslateTransform(-shakeX, -shakeY);

        // --- Overlays ---
        if (!_modeSelected)
        {
            DrawOverlay(g, 0.6f);
            DrawCenteredText(g, "P O N G", 56, FontStyle.Bold, Brushes.White, -90);
            DrawCenteredText(g, "Press 1 for Single Player", 16, FontStyle.Regular, Brushes.White, 0);
            DrawCenteredText(g, "Press 2 for Two Players", 16, FontStyle.Regular, Brushes.White, 35);
            DrawCenteredText(g, "F1 — Toggle FPS", 10, FontStyle.Regular, Brushes.Gray, 80);
        }
        else if (_singlePlayer && !_difficultySelected)
        {
            DrawOverlay(g, 0.5f);
            DrawCenteredText(g, "Select Difficulty", 28, FontStyle.Bold, Brushes.White, -60);
            DrawCenteredText(g, "[E]  Easy", 18, FontStyle.Regular, Brushes.LightGreen, 0);
            DrawCenteredText(g, "[M]  Medium", 18, FontStyle.Regular, Brushes.Yellow, 32);
            DrawCenteredText(g, "[H]  Hard", 18, FontStyle.Regular, Brushes.OrangeRed, 64);
        }
        else if (!_gameStarted)
        {
            string modeLabel = _singlePlayer
                ? $"1P vs AI ({_ai.Difficulty})" : "Player 1 vs Player 2";
            DrawCenteredText(g, modeLabel, 14, FontStyle.Regular, Brushes.Gray, 10);
            DrawCenteredText(g, "P1: W/A/S/D    P2: Arrows", 12, FontStyle.Regular, Brushes.Gray, 35);
            DrawCenteredText(g, "Press SPACE to start", 16, FontStyle.Regular, Brushes.White, 60);
        }
        else if (_paused)
        {
            DrawOverlay(g, 0.5f);
            DrawCenteredText(g, "PAUSED", 40, FontStyle.Bold, Brushes.White, -20);
            DrawCenteredText(g, "Press ESC to resume", 14, FontStyle.Regular, Brushes.Gray, 30);
        }
        else if (_matchOver)
        {
            DrawOverlay(g, 0.5f);
            string winner = _scoreLeft >= WinningScore
                ? "Player 1 Wins!" : (_singlePlayer ? "AI Wins!" : "Player 2 Wins!");
            DrawCenteredText(g, winner, 32, FontStyle.Bold, Brushes.White, -20);
            DrawCenteredText(g, "Press ENTER to restart", 14, FontStyle.Regular, Brushes.Gray, 30);
        }

        // FPS
        if (_showFps)
        {
            using var fpsFont = new Font("Consolas", 10);
            g.DrawString($"FPS: {_currentFps:F0}", fpsFont, Brushes.Lime, 5, 5);
        }
    }

    private static void DrawOverlay(Graphics g, float opacity)
    {
        using var brush = new SolidBrush(Color.FromArgb((int)(opacity * 255), 0, 0, 0));
        g.FillRectangle(brush, -10, -10, GameWidth + 20, GameHeight + 20);
    }

    private static void DrawPaddleTrail(Graphics g, Paddle paddle, Queue<float> trail)
    {
        int i = 0;
        foreach (float y in trail)
        {
            int alpha = (int)(15 + 40 * ((float)i / TrailLength));
            using var brush = new SolidBrush(Color.FromArgb(alpha, 100, 180, 255));
            g.FillRectangle(brush, paddle.X, y, paddle.Width, paddle.Height);
            i++;
        }
    }

    private static void DrawPaddleGlow(Graphics g, Paddle paddle)
    {
        using var glow = new SolidBrush(Color.FromArgb(30, 100, 180, 255));
        g.FillRectangle(glow, paddle.X - 2, paddle.Y - 2,
            paddle.Width + 4, paddle.Height + 4);
    }

    private void DrawCenteredText(Graphics g, string text, float fontSize, FontStyle style, Brush brush, float yOffset)
    {
        using var font = new Font("Consolas", fontSize, style);
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush,
            (GameWidth - size.Width) / 2, GameHeight / 2 + yOffset);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gameTimer.Stop();
            _gameTimer.Dispose();
            _bufGraphics.Dispose();
        }
        base.Dispose(disposing);
    }
}
