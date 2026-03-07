using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;

namespace AGIGame
{
    public class BathroomRoom : Room
    {
        private InteractiveObject hallwayDoor = null!;
        private InteractiveObject toilet = null!;
        private InteractiveObject sink = null!;
        private InteractiveObject shower = null!;
        private InteractiveObject mirror = null!;
        private InteractiveObject cabinet = null!;
        private InteractiveObject towel = null!;
        private bool towelTaken = false;

        public BathroomRoom(int width, int height) : base(width, height)
        {
            InitializeObjects();
        }

        private void InitializeObjects()
        {
            // Door to hallway
            hallwayDoor = new InteractiveObject(
                "Hallway Door",
                new Rectangle(540, 300, 60, 90),
                280, 390,
                true,
                RenderDoor,
                (inventory) => "ROOM_TRANSITION:Hallway"
            );
            objects.Add(hallwayDoor);

            // Toilet (left side)
            toilet = new InteractiveObject(
                "Toilet",
                new Rectangle(80, 200, 50, 60),
                180, 260,
                true,
                RenderToilet,
                (inventory) => "A standard white toilet. Clean and functional."
            );
            objects.Add(toilet);

            // Sink with counter (back wall, left)
            sink = new InteractiveObject(
                "Sink",
                new Rectangle(200, 100, 80, 65),
                100, 165,
                true,
                RenderSink,
                (inventory) => "A porcelain sink with chrome fixtures. The water runs clear."
            );
            objects.Add(sink);

            // Mirror above sink
            mirror = new InteractiveObject(
                "Mirror",
                new Rectangle(210, 40, 60, 50),
                40, 90,
                false,
                RenderMirror,
                (inventory) => "You see your reflection. You look ready for an adventure!"
            );
            objects.Add(mirror);

            // Medicine cabinet
            cabinet = new InteractiveObject(
                "Cabinet",
                new Rectangle(320, 60, 70, 80),
                60, 140,
                true,
                RenderCabinet,
                (inventory) => "The medicine cabinet contains basic first aid supplies and toiletries."
            );
            objects.Add(cabinet);

            // Shower/bathtub (right side)
            shower = new InteractiveObject(
                "Shower",
                new Rectangle(450, 150, 120, 110),
                150, 260,
                true,
                RenderShower,
                (inventory) => "A clean shower with a glass door. The water pressure is excellent."
            );
            objects.Add(shower);

            // Towel rack
            towel = new InteractiveObject(
                "Towel",
                new Rectangle(410, 170, 20, 36),
                165, 210,
                false,
                RenderTowel,
                (inventory) =>
                {
                    if (!towelTaken)
                    {
                        inventory.Add("Towel");
                        towelTaken = true;
                        return "You take a fresh, fluffy towel.";
                    }
                    return "You already have the towel.";
                }
            );
            objects.Add(towel);
        }

        public override void Render(Graphics g)
        {
            RetroGraphics.SetupRetroGraphics(g);

            // Back wall (white tiles)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(0, 0, Width, Height / 3),
                RetroGraphics.Palette.White);

            // Floor (light gray tiles with dithering)
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(0, Height / 3, Width, Height * 2 / 3),
                RetroGraphics.Palette.LightGray,
                RetroGraphics.Palette.White,
                3);

            // Small window
            DrawWindow(g, 100, 50, 40, 40);

            // Baseboards
            using (Pen basePen = new Pen(RetroGraphics.Palette.LightGray, 2))
            {
                g.DrawLine(basePen, 0, Height / 3, Width, Height / 3);
            }

            // Bath mat
            DrawBathMat(g, 150, 280, 80, 60);
        }

        private void DrawWindow(Graphics g, int x, int y, int w, int h)
        {
            // Frame (white)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 3, y - 3, w + 6, h + 6),
                RetroGraphics.Palette.White);

            // Frosted glass (light cyan)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.LightCyan);

            // Panes
            using (Pen panePen = new Pen(RetroGraphics.Palette.White, 2))
            {
                g.DrawRectangle(panePen, x, y, w, h);
                g.DrawLine(panePen, x + w / 2, y, x + w / 2, y + h);
                g.DrawLine(panePen, x, y + h / 2, x + w, y + h / 2);
            }
        }

        private void DrawBathMat(Graphics g, int x, int y, int w, int h)
        {
            // Mat (blue)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Blue);

            // Texture lines
            using (Pen texturePen = new Pen(Color.FromArgb(0, 0, 140), 1))
            {
                for (int i = 0; i < h; i += 4)
                {
                    g.DrawLine(texturePen, x, y + i, x + w, y + i);
                }
            }
        }

        private void RenderToilet(Graphics g)
        {
            int x = toilet.Bounds.X;
            int y = toilet.Bounds.Y;
            int w = toilet.Bounds.Width;
            int h = toilet.Bounds.Height;

            // Tank (white)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 6, y, w - 12, 22),
                RetroGraphics.Palette.White);

            // Bowl (white)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x, y + 18, w, h - 18),
                RetroGraphics.Palette.White);

            // Bowl rim (light gray)
            using (Pen rimPen = new Pen(RetroGraphics.Palette.LightGray, 2))
            {
                g.DrawEllipse(rimPen, x + 6, y + 24, w - 12, h - 30);
            }

            // Flush handle (gray rectangle)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w - 6, y + 6, 3, 8),
                RetroGraphics.Palette.DarkGray);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 6, y, w - 12, 22),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderSink(Graphics g)
        {
            int x = sink.Bounds.X;
            int y = sink.Bounds.Y;
            int w = sink.Bounds.Width;
            int h = sink.Bounds.Height;

            // Cabinet (brown)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y + 22, w, h - 22),
                RetroGraphics.Palette.Brown);

            // Countertop (light gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 2, y, w + 4, 22),
                RetroGraphics.Palette.LightGray);

            // Sink basin (white)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + 12, y + 4, w - 24, 16),
                RetroGraphics.Palette.White);

            // Faucet (gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w / 2 - 2, y - 6, 4, 8),
                RetroGraphics.Palette.DarkGray);
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + w / 2 - 4, y - 8, 8, 6),
                RetroGraphics.Palette.DarkGray);

            // Cabinet door
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 4, y + 28, w - 8, h - 32),
                RetroGraphics.Palette.DarkBrown,
                2);

            // Knob (yellow)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w / 2 - 2, y + h / 2 + 10, 4, 4),
                RetroGraphics.Palette.Yellow);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y + 22, w, h - 22),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderMirror(Graphics g)
        {
            int x = mirror.Bounds.X;
            int y = mirror.Bounds.Y;
            int w = mirror.Bounds.Width;
            int h = mirror.Bounds.Height;

            // Frame (light gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 3, y - 3, w + 6, h + 6),
                RetroGraphics.Palette.LightGray);

            // Mirror surface (dithered light cyan/white for reflection)
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.LightCyan,
                RetroGraphics.Palette.White,
                0);

            // Highlight
            using (Pen highlightPen = new Pen(RetroGraphics.Palette.White, 2))
            {
                g.DrawLine(highlightPen, x + 4, y + 4, x + 16, y + 4);
            }
        }

        private void RenderCabinet(Graphics g)
        {
            int x = cabinet.Bounds.X;
            int y = cabinet.Bounds.Y;
            int w = cabinet.Bounds.Width;
            int h = cabinet.Bounds.Height;

            // Cabinet body (white)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.White);

            // Door
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 4, y + 4, w - 8, h - 8),
                RetroGraphics.Palette.LightGray,
                2);

            // Handle (gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w - 10, y + h / 2 - 2, 4, 4),
                RetroGraphics.Palette.DarkGray);

            // Mirror/glass panel effect (light cyan)
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(x + 8, y + 8, w - 16, h - 16),
                RetroGraphics.Palette.LightCyan,
                RetroGraphics.Palette.White,
                0);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderShower(Graphics g)
        {
            int x = shower.Bounds.X;
            int y = shower.Bounds.Y;
            int w = shower.Bounds.Width;
            int h = shower.Bounds.Height;

            // Tub/shower base (white)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y + h - 28, w, 28),
                RetroGraphics.Palette.White);

            // Shower walls (white)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, 10, h - 28),
                RetroGraphics.Palette.White);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 10, y, w - 10, h - 28),
                RetroGraphics.Palette.White);

            // Glass door (dithered light cyan for transparency)
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(x + w - 6, y, 6, h - 28),
                RetroGraphics.Palette.LightCyan,
                RetroGraphics.Palette.White,
                0);

            // Shower head (gray)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + 18, y + 8, 10, 10),
                RetroGraphics.Palette.DarkGray);

            // Faucet handle (gray)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + 14, y + h - 48, 6, 6),
                RetroGraphics.Palette.DarkGray);

            // Outlines
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderTowel(Graphics g)
        {
            if (!towelTaken)
            {
                int x = towel.Bounds.X;
                int y = towel.Bounds.Y;
                int w = towel.Bounds.Width;
                int h = towel.Bounds.Height;

                // Towel rack bar (gray)
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(x - 4, y + h / 2 - 2, w + 8, 3),
                    RetroGraphics.Palette.DarkGray);

                // Towel (blue)
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(x, y, w, h),
                    RetroGraphics.Palette.Blue);

                // Towel folds (darker blue lines)
                using (Pen foldPen = new Pen(Color.FromArgb(0, 0, 140), 1))
                {
                    for (int i = 0; i < h; i += 6)
                    {
                        g.DrawLine(foldPen, x, y + i, x + w, y + i);
                    }
                }
            }
        }

        private void RenderDoor(Graphics g)
        {
            int x = hallwayDoor.Bounds.X;
            int y = hallwayDoor.Bounds.Y;
            int w = hallwayDoor.Bounds.Width;
            int h = hallwayDoor.Bounds.Height;

            // Door frame
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 4, y - 4, w + 8, h + 8),
                RetroGraphics.Palette.DarkBrown);

            // Door
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Brown);

            // Panels
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 4, y + 6, w - 8, h / 2 - 10),
                RetroGraphics.Palette.DarkBrown,
                2);
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 4, y + h / 2 + 2, w - 8, h / 2 - 10),
                RetroGraphics.Palette.DarkBrown,
                2);

            // Handle
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 6, y + h / 2 - 3, 6, 6),
                RetroGraphics.Palette.Yellow);

            // Label
            RetroGraphics.DrawRetroText(g, "EXIT", x + 18, y - 15,
                RetroGraphics.Palette.White, 8);
        }
    }
}
