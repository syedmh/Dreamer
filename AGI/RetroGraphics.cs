using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AGIGame
{
    public static class RetroGraphics
    {
        // Classic EGA/VGA color palette (16 colors)
        public static class Palette
        {
            public static readonly Color Black = Color.FromArgb(0, 0, 0);
            public static readonly Color Blue = Color.FromArgb(0, 0, 170);
            public static readonly Color Green = Color.FromArgb(0, 170, 0);
            public static readonly Color Cyan = Color.FromArgb(0, 170, 170);
            public static readonly Color Red = Color.FromArgb(170, 0, 0);
            public static readonly Color Magenta = Color.FromArgb(170, 0, 170);
            public static readonly Color Brown = Color.FromArgb(170, 85, 0);
            public static readonly Color LightGray = Color.FromArgb(170, 170, 170);
            public static readonly Color DarkGray = Color.FromArgb(85, 85, 85);
            public static readonly Color LightBlue = Color.FromArgb(85, 85, 255);
            public static readonly Color LightGreen = Color.FromArgb(85, 255, 85);
            public static readonly Color LightCyan = Color.FromArgb(85, 255, 255);
            public static readonly Color LightRed = Color.FromArgb(255, 85, 85);
            public static readonly Color LightMagenta = Color.FromArgb(255, 85, 255);
            public static readonly Color Yellow = Color.FromArgb(255, 255, 85);
            public static readonly Color White = Color.FromArgb(255, 255, 255);

            // Extended palette for more variety
            public static readonly Color Tan = Color.FromArgb(210, 180, 140);
            public static readonly Color Beige = Color.FromArgb(245, 222, 179);
            public static readonly Color DarkBrown = Color.FromArgb(101, 67, 33);
            public static readonly Color SkyBlue = Color.FromArgb(135, 206, 250);
            public static readonly Color Pink = Color.FromArgb(255, 192, 203);
        }

        // Draw dithered rectangle (replaces gradients)
        public static void FillDitheredRectangle(Graphics g, Rectangle rect, Color color1, Color color2, int pattern = 0)
        {
            using (Bitmap bmp = new Bitmap(rect.Width, rect.Height))
            {
                for (int y = 0; y < rect.Height; y++)
                {
                    for (int x = 0; x < rect.Width; x++)
                    {
                        bool useDark = pattern switch
                        {
                            0 => (x + y) % 2 == 0,  // Checkerboard
                            1 => x % 2 == 0,        // Vertical lines
                            2 => y % 2 == 0,        // Horizontal lines
                            3 => (x % 4 < 2) == (y % 4 < 2), // Larger checkerboard
                            _ => (x + y) % 2 == 0
                        };
                        bmp.SetPixel(x, y, useDark ? color1 : color2);
                    }
                }
                g.DrawImage(bmp, rect);
            }
        }

        // Draw a solid rectangle with retro color
        public static void FillRetroRectangle(Graphics g, Rectangle rect, Color color)
        {
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillRectangle(brush, rect);
            }
        }

        // Draw retro-style outline
        public static void DrawRetroRectangle(Graphics g, Rectangle rect, Color color, int width = 1)
        {
            using (Pen pen = new Pen(color, width))
            {
                g.DrawRectangle(pen, rect);
            }
        }

        // Draw retro-style ellipse
        public static void FillRetroEllipse(Graphics g, Rectangle rect, Color color)
        {
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, rect);
            }
        }

        // Draw retro polygon
        public static void FillRetroPolygon(Graphics g, Point[] points, Color color)
        {
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillPolygon(brush, points);
            }
        }

        // Create a dithered brush pattern
        public static Brush CreateDitheredBrush(Color color1, Color color2)
        {
            Bitmap pattern = new Bitmap(2, 2);
            pattern.SetPixel(0, 0, color1);
            pattern.SetPixel(1, 1, color1);
            pattern.SetPixel(0, 1, color2);
            pattern.SetPixel(1, 0, color2);
            return new TextureBrush(pattern);
        }

        // Setup graphics for retro rendering
        public static void SetupRetroGraphics(Graphics g)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.CompositingQuality = CompositingQuality.HighSpeed;
        }

        // Draw text with retro font
        public static void DrawRetroText(Graphics g, string text, int x, int y, Color color, int size = 8)
        {
            using (Font font = new Font("Courier New", size, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixel;
                g.DrawString(text, font, brush, x, y);
            }
        }
    }
}
