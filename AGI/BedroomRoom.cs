using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;

namespace AGIGame
{
    public class BedroomRoom : Room
    {
        private InteractiveObject bed = null!;
        private InteractiveObject wardrobe = null!;
        private InteractiveObject dresser = null!;
        private InteractiveObject wallet = null!;
        private InteractiveObject door = null!;
        private bool walletTaken = false;

        public BedroomRoom(int width, int height) : base(width, height)
        {
            InitializeObjects();
        }

        private void InitializeObjects()
        {
            // Create bed (left side, middle depth)
            bed = new InteractiveObject(
                "Bed",
                new Rectangle(80, 220, 140, 60),
                200, 280,
                true,
                RenderBed,
                (inventory) => "You sit on the bed. It's quite comfortable, but you have things to do."
            );
            objects.Add(bed);

            // Create wardrobe (back wall, top area)
            wardrobe = new InteractiveObject(
                "Wardrobe",
                new Rectangle(270, 80, 100, 80),
                80, 160,
                true,
                RenderWardrobe,
                (inventory) =>
                {
                    return "You open the wardrobe drawer. It's full of neatly folded clothes.";
                }
            );
            objects.Add(wardrobe);

            // Create dresser (right side, middle depth)
            dresser = new InteractiveObject(
                "Dresser",
                new Rectangle(480, 180, 120, 70),
                160, 250,
                true,
                RenderDresser,
                null
            );
            objects.Add(dresser);

            // Create wallet on dresser
            wallet = new InteractiveObject(
                "Wallet",
                new Rectangle(530, 160, 16, 12),
                150, 170,
                false,
                RenderWallet,
                (inventory) =>
                {
                    if (!walletTaken)
                    {
                        inventory.Add("Wallet");
                        walletTaken = true;
                        return "You pick up the leather wallet. There's $50 and your ID inside.";
                    }
                    return "You already have the wallet.";
                }
            );
            objects.Add(wallet);

            // Door to hallway (bottom right)
            door = new InteractiveObject(
                "Door",
                new Rectangle(540, 300, 60, 90),
                280, 390,
                true,
                RenderDoor,
                (inventory) => "ROOM_TRANSITION:Hallway"
            );
            objects.Add(door);
        }

        public override void Render(Graphics g)
        {
            RetroGraphics.SetupRetroGraphics(g);

            // Draw floor with dithering
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(0, Height / 3, Width, Height * 2 / 3),
                RetroGraphics.Palette.Brown,
                RetroGraphics.Palette.DarkBrown,
                3);

            // Draw back wall (solid color)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(0, 0, Width, Height / 3),
                RetroGraphics.Palette.Tan);

            // Draw rug in center (red)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(220, 280, 200, 120),
                RetroGraphics.Palette.Red);

            // Rug border
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(220, 280, 200, 120),
                RetroGraphics.Palette.DarkGray,
                2);

            // Draw window
            DrawWindow(g, 400, 30, 60, 50);

            // Base line
            using (Pen pen = new Pen(RetroGraphics.Palette.DarkBrown, 2))
            {
                g.DrawLine(pen, 0, Height / 3, Width, Height / 3);
            }
        }

        private void DrawWindow(Graphics g, int x, int y, int w, int h)
        {
            // Window frame
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 3, y - 3, w + 6, h + 6),
                RetroGraphics.Palette.DarkBrown);

            // Glass (cyan for sky)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Cyan);

            // Window panes
            using (Pen pen = new Pen(RetroGraphics.Palette.DarkBrown, 2))
            {
                g.DrawRectangle(pen, x, y, w, h);
                g.DrawLine(pen, x + w / 2, y, x + w / 2, y + h);
                g.DrawLine(pen, x, y + h / 2, x + w, y + h / 2);
            }
        }

        private void RenderBed(Graphics g)
        {
            int x = bed.Bounds.X;
            int y = bed.Bounds.Y;
            int w = bed.Bounds.Width;
            int h = bed.Bounds.Height;

            // Bed frame
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.DarkBrown);

            // Mattress
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 4, y + 4, w - 8, h - 10),
                RetroGraphics.Palette.Beige);

            // Pillow
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 8, y + 8, 28, 14),
                RetroGraphics.Palette.White);

            // Blanket
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w - 35, y + h - 30, 28, 20),
                RetroGraphics.Palette.Blue);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderWardrobe(Graphics g)
        {
            int x = wardrobe.Bounds.X;
            int y = wardrobe.Bounds.Y;
            int w = wardrobe.Bounds.Width;
            int h = wardrobe.Bounds.Height;

            // Main body
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Brown);

            // Door panels
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 4, y + 4, w / 2 - 6, h - 30),
                RetroGraphics.Palette.DarkBrown,
                2);
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + w / 2 + 2, y + 4, w / 2 - 6, h - 30),
                RetroGraphics.Palette.DarkBrown,
                2);

            // Door handles
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w / 4 - 2, y + h / 2 - 2, 4, 4),
                RetroGraphics.Palette.Yellow);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 3 * w / 4 - 2, y + h / 2 - 2, 4, 4),
                RetroGraphics.Palette.Yellow);

            // Drawer at bottom
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 8, y + h - 24, w - 16, 20),
                RetroGraphics.Palette.DarkBrown,
                2);

            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w / 2 - 2, y + h - 14, 4, 4),
                RetroGraphics.Palette.Yellow);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderDresser(Graphics g)
        {
            int x = dresser.Bounds.X;
            int y = dresser.Bounds.Y;
            int w = dresser.Bounds.Width;
            int h = dresser.Bounds.Height;

            // Cabinet body
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Brown);

            // Countertop (gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 4, y - 8, w + 8, 10),
                RetroGraphics.Palette.LightGray);

            // Drawers
            int drawerHeight = h / 3;
            for (int i = 0; i < 3; i++)
            {
                int drawerY = y + (i * drawerHeight);
                RetroGraphics.DrawRetroRectangle(g,
                    new Rectangle(x + 4, drawerY + 2, w - 8, drawerHeight - 4),
                    RetroGraphics.Palette.DarkBrown,
                    2);

                // Handle
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(x + w / 2 - 3, drawerY + drawerHeight / 2 - 2, 6, 4),
                    RetroGraphics.Palette.Yellow);
            }

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderWallet(Graphics g)
        {
            if (!walletTaken)
            {
                int x = wallet.Bounds.X;
                int y = wallet.Bounds.Y;
                int w = wallet.Bounds.Width;
                int h = wallet.Bounds.Height;

                // Wallet body
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(x, y, w, h),
                    RetroGraphics.Palette.DarkBrown);

                // Wallet detail
                RetroGraphics.DrawRetroRectangle(g,
                    new Rectangle(x, y, w, h),
                    RetroGraphics.Palette.Black,
                    1);

                using (Pen pen = new Pen(RetroGraphics.Palette.Black, 1))
                {
                    g.DrawLine(pen, x, y + h / 2, x + w, y + h / 2);
                }
            }
        }

        private void RenderDoor(Graphics g)
        {
            int x = door.Bounds.X;
            int y = door.Bounds.Y;
            int w = door.Bounds.Width;
            int h = door.Bounds.Height;

            // Door frame
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 4, y - 4, w + 8, h + 8),
                RetroGraphics.Palette.DarkBrown);

            // Door
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Brown);

            // Door panels
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 4, y + 6, w - 8, h / 2 - 10),
                RetroGraphics.Palette.DarkBrown,
                2);
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 4, y + h / 2 + 2, w - 8, h / 2 - 10),
                RetroGraphics.Palette.DarkBrown,
                2);

            // Door handle
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 6, y + h / 2 - 3, 6, 6),
                RetroGraphics.Palette.Yellow);

            // Label
            RetroGraphics.DrawRetroText(g, "EXIT", x + 18, y - 15,
                RetroGraphics.Palette.White, 8);
        }
    }
}
