using System;
using System.Drawing;

namespace AGIGame
{
    public class Player
    {
        public int X { get; private set; }
        public int Y { get; private set; }
        public int Z { get; private set; }
        public int Width { get; } = 32;
        public int Height { get; } = 48;

        private int direction; // 0=down, 1=up, 2=left, 3=right
        private int animFrame;
        private int animCounter;
        private float scale = 1.0f;

        public Player(int x, int y, int z = 0)
        {
            X = x;
            Y = y;
            Z = z;
            direction = 0;
            animFrame = 0;
            animCounter = 0;
        }

        public void Move(int dx, int dy, Room room)
        {
            if (dx == 0 && dy == 0) return;

            // Determine direction
            if (Math.Abs(dx) > Math.Abs(dy))
                direction = dx > 0 ? 3 : 2; // right : left
            else
                direction = dy > 0 ? 0 : 1; // down : up

            // Update animation
            animCounter++;
            if (animCounter > 6)
            {
                animFrame = (animFrame + 1) % 4;
                animCounter = 0;
            }

            // Calculate new position
            int newX = X + dx;
            int newY = Y + dy;

            // Calculate depth-based scaling (characters further back are smaller)
            scale = 0.6f + (newY / (float)room.Height) * 0.7f;

            // Check room boundaries
            if (newX < 40) newX = 40;
            if (newX > room.Width - 40) newX = room.Width - 40;
            if (newY < 80) newY = 80;
            if (newY > room.Height - 50) newY = room.Height - 50;

            // Check collisions with room objects
            Rectangle playerRect = GetBoundingBox(newX, newY);
            if (!room.CheckCollision(playerRect, newY))
            {
                X = newX;
                Y = newY;
            }
        }

        public Rectangle GetBoundingBox(int? x = null, int? y = null)
        {
            int posX = x ?? X;
            int posY = y ?? Y;
            int w = (int)(Width * scale);
            int h = (int)(Height * scale);
            return new Rectangle(posX - w / 2, posY - h + 2, w, h / 2);
        }

        public void Render(Graphics g)
        {
            RetroGraphics.SetupRetroGraphics(g);

            int w = (int)(Width * scale);
            int h = (int)(Height * scale);
            int centerX = X;
            int baseY = Y;

            // Draw shadow (simple oval)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(centerX - w / 2, baseY - 2, w, 4),
                Color.FromArgb(100, 0, 0, 0));

            // Draw character based on direction
            switch (direction)
            {
                case 0: // Down (facing camera)
                    RenderFront(g, centerX, baseY, w, h);
                    break;
                case 1: // Up (facing away)
                    RenderBack(g, centerX, baseY, w, h);
                    break;
                case 2: // Left
                    RenderLeft(g, centerX, baseY, w, h);
                    break;
                case 3: // Right
                    RenderRight(g, centerX, baseY, w, h);
                    break;
            }
        }

        private void RenderFront(Graphics g, int cx, int by, int w, int h)
        {
            // Hair/head top
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 3, by - h, w * 2 / 3, h / 5),
                RetroGraphics.Palette.DarkBrown);

            // Face
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 4, by - h + h / 5, w / 2, h / 4),
                RetroGraphics.Palette.Tan);

            // Eyes (bigger and more visible)
            g.FillRectangle(Brushes.Black, cx - w / 6, by - h + h / 4 - 2, 3, 3);
            g.FillRectangle(Brushes.Black, cx + w / 12, by - h + h / 4 - 2, 3, 3);

            // Neck
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 8, by - h + h * 9 / 20, w / 4, h / 10),
                RetroGraphics.Palette.Tan);

            // Body/Shirt (blue)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 2 + 2, by - h / 2, w - 4, h / 3 + 2),
                RetroGraphics.Palette.Blue);

            // Belt/waist line
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 2 + 2, by - h / 5 - 2, w - 4, 3),
                RetroGraphics.Palette.Brown);

            // Legs with walk animation (brown pants)
            int legOffset = (animFrame % 2) * 3 - 1;
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 4 + legOffset, by - h / 5, w / 3 - 2, h / 5),
                RetroGraphics.Palette.Brown);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - 2 - legOffset, by - h / 5, w / 3 - 2, h / 5),
                RetroGraphics.Palette.Brown);

            // Feet/shoes (black)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 4 + legOffset, by - 4, w / 3 - 2, 4),
                RetroGraphics.Palette.Black);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - 2 - legOffset, by - 4, w / 3 - 2, 4),
                RetroGraphics.Palette.Black);

            // Arms (tan skin)
            int armSwing = (animFrame % 2) * 2 - 1;
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 2 - 2, by - h / 2 + 4 + armSwing, w / 6, h / 3 - 4),
                RetroGraphics.Palette.Tan);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx + w / 2 - w / 6 + 2, by - h / 2 + 4 - armSwing, w / 6, h / 3 - 4),
                RetroGraphics.Palette.Tan);

            // Hands (tan)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 2 - 2, by - h / 6 + armSwing, w / 8, w / 8),
                RetroGraphics.Palette.Tan);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx + w / 2 - w / 8 + 2, by - h / 6 - armSwing, w / 8, w / 8),
                RetroGraphics.Palette.Tan);
        }

        private void RenderBack(Graphics g, int cx, int by, int w, int h)
        {
            // Hair/head (back view)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 3, by - h, w * 2 / 3, h / 4 + 4),
                RetroGraphics.Palette.DarkBrown);

            // Neck (barely visible from back)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 10, by - h + h / 4 + 2, w / 5, h / 12),
                RetroGraphics.Palette.Tan);

            // Body/Shirt (blue)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 2 + 2, by - h / 2, w - 4, h / 3 + 2),
                RetroGraphics.Palette.Blue);

            // Belt
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 2 + 2, by - h / 5 - 2, w - 4, 3),
                RetroGraphics.Palette.Brown);

            // Legs (brown pants)
            int legOffset = (animFrame % 2) * 3 - 1;
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 4 - legOffset, by - h / 5, w / 3 - 2, h / 5),
                RetroGraphics.Palette.Brown);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - 2 + legOffset, by - h / 5, w / 3 - 2, h / 5),
                RetroGraphics.Palette.Brown);

            // Feet
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 4 - legOffset, by - 4, w / 3 - 2, 4),
                RetroGraphics.Palette.Black);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - 2 + legOffset, by - 4, w / 3 - 2, 4),
                RetroGraphics.Palette.Black);

            // Arms (mostly hidden behind body from back view)
            int armSwing = (animFrame % 2) * 2;
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 2 - 1, by - h / 2 + 8 + armSwing, w / 8, h / 4),
                RetroGraphics.Palette.Blue);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx + w / 2 - w / 8 + 1, by - h / 2 + 8 - armSwing, w / 8, h / 4),
                RetroGraphics.Palette.Blue);
        }

        private void RenderLeft(Graphics g, int cx, int by, int w, int h)
        {
            // Head
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 4, by - h + h / 8, w / 2, h / 4),
                RetroGraphics.Palette.Tan);

            // Hair
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 4, by - h + h / 8, w / 2, h / 8),
                RetroGraphics.Palette.DarkBrown);

            // Body
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 3, by - h + h / 3, w * 2 / 3, h / 3),
                RetroGraphics.Palette.Blue);

            // Legs
            if (animFrame % 2 == 0)
            {
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(cx - w / 4, by - h / 3, w / 5, h / 3),
                    RetroGraphics.Palette.Brown);
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(cx - w / 12, by - h / 3, w / 5, h / 3),
                    RetroGraphics.Palette.Brown);
            }
            else
            {
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(cx - w / 12, by - h / 3, w / 5, h / 3),
                    RetroGraphics.Palette.Brown);
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(cx - w / 4, by - h / 3, w / 5, h / 3),
                    RetroGraphics.Palette.Brown);
            }

            // Arm
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 3, by - h + h / 2, w / 5, h / 4),
                RetroGraphics.Palette.Tan);

            // Eye
            g.FillRectangle(Brushes.Black, cx - w / 8, by - h + h / 5, 2, 2);
        }

        private void RenderRight(Graphics g, int cx, int by, int w, int h)
        {
            // Head
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 4, by - h + h / 8, w / 2, h / 4),
                RetroGraphics.Palette.Tan);

            // Hair
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 4, by - h + h / 8, w / 2, h / 8),
                RetroGraphics.Palette.DarkBrown);

            // Body
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx - w / 3, by - h + h / 3, w * 2 / 3, h / 3),
                RetroGraphics.Palette.Blue);

            // Legs
            if (animFrame % 2 == 0)
            {
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(cx + w / 20, by - h / 3, w / 5, h / 3),
                    RetroGraphics.Palette.Brown);
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(cx - w / 10, by - h / 3, w / 5, h / 3),
                    RetroGraphics.Palette.Brown);
            }
            else
            {
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(cx - w / 10, by - h / 3, w / 5, h / 3),
                    RetroGraphics.Palette.Brown);
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(cx + w / 20, by - h / 3, w / 5, h / 3),
                    RetroGraphics.Palette.Brown);
            }

            // Arm
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(cx + w / 6, by - h + h / 2, w / 5, h / 4),
                RetroGraphics.Palette.Tan);

            // Eye
            g.FillRectangle(Brushes.Black, cx + w / 8, by - h + h / 5, 2, 2);
        }

        public Rectangle GetInteractionArea()
        {
            return new Rectangle(X - 40, Y - 40, 80, 80);
        }

        public int GetDepth()
        {
            return Y;
        }
    }
}
