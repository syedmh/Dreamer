using System;
using System.Drawing;
using System.Windows.Forms;

namespace AGIGame
{
    public class GameWindow : Form
    {
        private GameEngine gameEngine;
        private System.Windows.Forms.Timer gameTimer;
        private BufferedGraphicsContext context;
        private BufferedGraphics buffer;
        private TextBox commandInput;

        public GameWindow()
        {
            this.Text = "AGI Adventure Game - Type commands below";
            this.ClientSize = new Size(640, 510);  // Increased height for textbox
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.DoubleBuffered = true;

            // Initialize graphics buffer for smooth rendering
            context = BufferedGraphicsManager.Current;
            buffer = context.Allocate(this.CreateGraphics(), new Rectangle(0, 0, 640, 480));

            // Initialize game engine
            gameEngine = new GameEngine(640, 480);

            // Add text input box at bottom
            commandInput = new TextBox();
            commandInput.Location = new Point(10, 485);
            commandInput.Size = new Size(620, 20);
            commandInput.Font = new Font("Courier New", 10, FontStyle.Bold);
            commandInput.BackColor = Color.Black;
            commandInput.ForeColor = Color.White;
            commandInput.KeyDown += OnCommandKeyDown;
            this.Controls.Add(commandInput);

            // Setup game timer for 60 FPS
            gameTimer = new System.Windows.Forms.Timer();
            gameTimer.Interval = 16; // ~60 FPS
            gameTimer.Tick += GameLoop;
            gameTimer.Start();

            // Handle keyboard input
            this.KeyDown += OnKeyDown;
            this.KeyUp += OnKeyUp;
            this.KeyPreview = true;  // Form receives all keys first

            this.Paint += (s, e) => { /* Handled by timer */ };
        }

        private void GameLoop(object? sender, EventArgs e)
        {
            // Update game logic
            gameEngine.Update();

            // Render to buffer with retro settings
            Graphics g = buffer.Graphics;

            // Disable anti-aliasing for pixelated look
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;

            gameEngine.Render(g);

            // Draw buffer to screen
            buffer.Render(this.CreateGraphics());
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            // Only handle arrow keys and space for game movement
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ||
                e.KeyCode == Keys.Up || e.KeyCode == Keys.Down ||
                e.KeyCode == Keys.Space)
            {
                gameEngine.HandleKeyDown(e.KeyCode);
                e.Handled = true;
            }
            // Let other keys pass through to textbox
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            // Only handle arrow keys and space for game movement
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ||
                e.KeyCode == Keys.Up || e.KeyCode == Keys.Down ||
                e.KeyCode == Keys.Space)
            {
                gameEngine.HandleKeyUp(e.KeyCode);
                e.Handled = true;
            }
            // Let other keys pass through to textbox
        }

        private void OnCommandKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                string command = commandInput.Text.Trim();
                if (!string.IsNullOrEmpty(command))
                {
                    gameEngine.ProcessTextCommand(command);
                    commandInput.Clear();
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            gameTimer.Stop();
            buffer?.Dispose();
            context?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
