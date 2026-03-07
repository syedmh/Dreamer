using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;

namespace AGIGame
{
    public class KitchenRoom : Room
    {
        private InteractiveObject hallwayDoor = null!;
        private InteractiveObject refrigerator = null!;
        private InteractiveObject stove = null!;
        private InteractiveObject sink = null!;
        private InteractiveObject counter = null!;
        private InteractiveObject apple = null!;
        private InteractiveObject keys = null!;
        private bool appleTaken = false;
        private bool keysTaken = false;

        public KitchenRoom(int width, int height) : base(width, height)
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

            // Refrigerator (left side)
            refrigerator = new InteractiveObject(
                "Refrigerator",
                new Rectangle(40, 120, 80, 120),
                120, 240,
                true,
                RenderRefrigerator,
                (inventory) => "You open the refrigerator. It's stocked with food and drinks. Looks like you won't starve today!"
            );
            objects.Add(refrigerator);

            // Stove (back wall, left)
            stove = new InteractiveObject(
                "Stove",
                new Rectangle(180, 100, 90, 70),
                100, 170,
                true,
                RenderStove,
                (inventory) => "The stove is clean and modern. All burners are off."
            );
            objects.Add(stove);

            // Kitchen counter (back wall, center)
            counter = new InteractiveObject(
                "Counter",
                new Rectangle(320, 110, 120, 60),
                110, 170,
                true,
                RenderCounter,
                (inventory) => "A clean granite countertop. Perfect for meal prep."
            );
            objects.Add(counter);

            // Sink (back wall, right)
            sink = new InteractiveObject(
                "Sink",
                new Rectangle(480, 100, 70, 60),
                100, 160,
                true,
                RenderSink,
                (inventory) => "A stainless steel sink. The dishes are done."
            );
            objects.Add(sink);

            // Apple on counter
            apple = new InteractiveObject(
                "Apple",
                new Rectangle(365, 100, 12, 12),
                95, 105,
                false,
                RenderApple,
                (inventory) =>
                {
                    if (!appleTaken)
                    {
                        inventory.Add("Apple");
                        appleTaken = true;
                        return "You take the fresh red apple. A healthy snack!";
                    }
                    return "You already took the apple.";
                }
            );
            objects.Add(apple);

            // Car keys on counter
            keys = new InteractiveObject(
                "Car Keys",
                new Rectangle(390, 102, 16, 10),
                97, 107,
                false,
                RenderKeys,
                (inventory) =>
                {
                    if (!keysTaken)
                    {
                        inventory.Add("Car Keys");
                        keysTaken = true;
                        return "You grab your car keys. Now you can leave the house!";
                    }
                    return "You already have the keys.";
                }
            );
            objects.Add(keys);
        }

        public override void Render(Graphics g)
        {
            RetroGraphics.SetupRetroGraphics(g);

            // Back wall (white/light gray for kitchen)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(0, 0, Width, Height / 3),
                RetroGraphics.Palette.White);

            // Floor with dithering (light gray tiles)
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(0, Height / 3, Width, Height * 2 / 3),
                RetroGraphics.Palette.LightGray,
                RetroGraphics.Palette.White,
                3);

            // Window above sink
            DrawWindow(g, 490, 40, 50, 40);

            // Baseboards
            using (Pen basePen = new Pen(RetroGraphics.Palette.DarkGray, 2))
            {
                g.DrawLine(basePen, 0, Height / 3, Width, Height / 3);
            }
        }

        private void DrawWindow(Graphics g, int x, int y, int w, int h)
        {
            // Frame (white)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 3, y - 3, w + 6, h + 6),
                RetroGraphics.Palette.White);

            // Glass (cyan for sky)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Cyan);

            // Panes
            using (Pen pen = new Pen(RetroGraphics.Palette.White, 2))
            {
                g.DrawRectangle(pen, x, y, w, h);
                g.DrawLine(pen, x + w / 2, y, x + w / 2, y + h);
                g.DrawLine(pen, x, y + h / 2, x + w, y + h / 2);
            }
        }

        private void RenderRefrigerator(Graphics g)
        {
            int x = refrigerator.Bounds.X;
            int y = refrigerator.Bounds.Y;
            int w = refrigerator.Bounds.Width;
            int h = refrigerator.Bounds.Height;

            // Main body (white)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.White);

            // Door separation
            using (Pen pen = new Pen(RetroGraphics.Palette.LightGray, 2))
            {
                g.DrawLine(pen, x, y + h / 3, x + w, y + h / 3);
            }

            // Handles (gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w - 8, y + h / 6 - 3, 4, 10),
                RetroGraphics.Palette.DarkGray);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w - 8, y + h / 2 - 3, 4, 10),
                RetroGraphics.Palette.DarkGray);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderStove(Graphics g)
        {
            int x = stove.Bounds.X;
            int y = stove.Bounds.Y;
            int w = stove.Bounds.Width;
            int h = stove.Bounds.Height;

            // Main body (dark gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.DarkGray);

            // Burners (black circles)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + 12, y + 15, 24, 24),
                RetroGraphics.Palette.Black);
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + w - 36, y + 15, 24, 24),
                RetroGraphics.Palette.Black);

            // Oven door (lighter gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 6, y + 45, w - 12, h - 50),
                RetroGraphics.Palette.LightGray);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderCounter(Graphics g)
        {
            int x = counter.Bounds.X;
            int y = counter.Bounds.Y;
            int w = counter.Bounds.Width;
            int h = counter.Bounds.Height;

            // Cabinet body (brown)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y + 15, w, h - 15),
                RetroGraphics.Palette.Brown);

            // Countertop (gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 4, y - 6, w + 8, 20),
                RetroGraphics.Palette.LightGray);

            // Cabinet doors
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 4, y + 20, w / 2 - 6, h - 25),
                RetroGraphics.Palette.DarkBrown,
                2);
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + w / 2 + 2, y + 20, w / 2 - 6, h - 25),
                RetroGraphics.Palette.DarkBrown,
                2);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y + 15, w, h - 15),
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
                new Rectangle(x, y + 20, w, h - 20),
                RetroGraphics.Palette.Brown);

            // Countertop (gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 4, y - 6, w + 8, 24),
                RetroGraphics.Palette.LightGray);

            // Sink basin (white)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + 8, y + 4, w - 16, 12),
                RetroGraphics.Palette.White);

            // Faucet (gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w / 2 - 2, y - 6, 4, 8),
                RetroGraphics.Palette.DarkGray);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y + 20, w, h - 20),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderApple(Graphics g)
        {
            if (!appleTaken)
            {
                int x = apple.Bounds.X;
                int y = apple.Bounds.Y;
                int w = apple.Bounds.Width;
                int h = apple.Bounds.Height;

                // Apple body (red)
                RetroGraphics.FillRetroEllipse(g,
                    new Rectangle(x, y, w, h),
                    RetroGraphics.Palette.Red);

                // Stem (brown)
                using (Pen stemPen = new Pen(RetroGraphics.Palette.Brown, 2))
                {
                    g.DrawLine(stemPen, x + w / 2, y, x + w / 2, y - 2);
                }
            }
        }

        private void RenderKeys(Graphics g)
        {
            if (!keysTaken)
            {
                int x = keys.Bounds.X;
                int y = keys.Bounds.Y;
                int w = keys.Bounds.Width;
                int h = keys.Bounds.Height;

                // Key ring (gray circle)
                RetroGraphics.FillRetroEllipse(g,
                    new Rectangle(x, y, 6, 6),
                    RetroGraphics.Palette.DarkGray);

                // Keys (yellow)
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(x + 6, y + 1, 8, 3),
                    RetroGraphics.Palette.Yellow);
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(x + 6, y + 5, 10, 3),
                    RetroGraphics.Palette.Yellow);
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
