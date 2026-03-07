using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;

namespace AGIGame
{
    public class LivingRoom : Room
    {
        private InteractiveObject hallwayDoor = null!;
        private InteractiveObject sofa = null!;
        private InteractiveObject tv = null!;
        private InteractiveObject bookshelf = null!;
        private InteractiveObject coffeeTable = null!;
        private InteractiveObject book = null!;
        private bool bookTaken = false;

        public LivingRoom(int width, int height) : base(width, height)
        {
            InitializeObjects();
        }

        private void InitializeObjects()
        {
            // Door to hallway
            hallwayDoor = new InteractiveObject(
                "Hallway Door",
                new Rectangle(50, 300, 60, 90),
                280, 390,
                true,
                RenderDoor,
                (inventory) => "ROOM_TRANSITION:Hallway"
            );
            objects.Add(hallwayDoor);

            // Sofa (middle-right area)
            sofa = new InteractiveObject(
                "Sofa",
                new Rectangle(420, 220, 160, 70),
                200, 290,
                true,
                RenderSofa,
                (inventory) => "A comfortable leather sofa. Perfect for relaxing after a long day."
            );
            objects.Add(sofa);

            // TV stand (back wall, center)
            tv = new InteractiveObject(
                "TV",
                new Rectangle(280, 80, 100, 80),
                80, 160,
                true,
                RenderTV,
                (inventory) => "A large flat-screen TV. It's turned off right now."
            );
            objects.Add(tv);

            // Bookshelf (left side)
            bookshelf = new InteractiveObject(
                "Bookshelf",
                new Rectangle(50, 140, 80, 100),
                140, 240,
                true,
                RenderBookshelf,
                (inventory) => "A wooden bookshelf filled with novels, biographies, and reference books."
            );
            objects.Add(bookshelf);

            // Coffee table (in front of sofa)
            coffeeTable = new InteractiveObject(
                "Coffee Table",
                new Rectangle(350, 250, 100, 50),
                230, 300,
                true,
                RenderCoffeeTable,
                (inventory) => "A modern glass coffee table."
            );
            objects.Add(coffeeTable);

            // Book on coffee table
            book = new InteractiveObject(
                "Book",
                new Rectangle(380, 240, 16, 12),
                235, 245,
                false,
                RenderBook,
                (inventory) =>
                {
                    if (!bookTaken)
                    {
                        inventory.Add("Mystery Novel");
                        bookTaken = true;
                        return "You pick up 'Murder at Midnight' - a thrilling mystery novel.";
                    }
                    return "You already took the book.";
                }
            );
            objects.Add(book);
        }

        public override void Render(Graphics g)
        {
            RetroGraphics.SetupRetroGraphics(g);

            // Back wall (beige)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(0, 0, Width, Height / 3),
                RetroGraphics.Palette.Beige);

            // Floor with dithering (brown hardwood)
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(0, Height / 3, Width, Height * 2 / 3),
                RetroGraphics.Palette.Brown,
                RetroGraphics.Palette.DarkBrown,
                1);

            // Window
            DrawWindow(g, 450, 40, 80, 60);

            // Area rug
            DrawAreaRug(g, 250, 260, 280, 140);

            // Baseboards
            using (Pen basePen = new Pen(RetroGraphics.Palette.DarkBrown, 2))
            {
                g.DrawLine(basePen, 0, Height / 3, Width, Height / 3);
            }
        }

        private void DrawWindow(Graphics g, int x, int y, int w, int h)
        {
            // Frame (brown)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 4, y - 4, w + 8, h + 8),
                RetroGraphics.Palette.DarkBrown);

            // Glass (cyan for sky)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Cyan);

            // Panes
            using (Pen panePen = new Pen(RetroGraphics.Palette.DarkBrown, 2))
            {
                g.DrawRectangle(panePen, x, y, w, h);
                g.DrawLine(panePen, x + w / 2, y, x + w / 2, y + h);
                g.DrawLine(panePen, x, y + h / 2, x + w, y + h / 2);
            }

            // Simple curtains (dark gray on sides)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 4, y - 4, 8, h + 8),
                RetroGraphics.Palette.DarkGray);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w - 4, y - 4, 8, h + 8),
                RetroGraphics.Palette.DarkGray);
        }

        private void DrawAreaRug(Graphics g, int x, int y, int w, int h)
        {
            // Rug base (tan)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Tan);

            // Border (brown)
            using (SolidBrush borderBrush = new SolidBrush(Color.FromArgb(120, 80, 60)))
            {
                g.FillRectangle(borderBrush, x, y, w, 6);
                g.FillRectangle(borderBrush, x, y + h - 6, w, 6);
                g.FillRectangle(borderBrush, x, y, 6, h);
                g.FillRectangle(borderBrush, x + w - 6, y, 6, h);
            }

            // Pattern
            using (Pen patternPen = new Pen(RetroGraphics.Palette.Brown, 1))
            {
                g.DrawRectangle(patternPen, x + 15, y + 15, w - 30, h - 30);
            }
        }

        private void RenderSofa(Graphics g)
        {
            int x = sofa.Bounds.X;
            int y = sofa.Bounds.Y;
            int w = sofa.Bounds.Width;
            int h = sofa.Bounds.Height;

            // Sofa body (brown)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y + 10, w, h - 10),
                RetroGraphics.Palette.Brown);

            // Backrest (darker brown)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, 15),
                RetroGraphics.Palette.DarkBrown);

            // Armrests
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y + 8, 12, h - 8),
                RetroGraphics.Palette.DarkBrown);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w - 12, y + 8, 12, h - 8),
                RetroGraphics.Palette.DarkBrown);

            // Cushion lines
            using (Pen linePen = new Pen(RetroGraphics.Palette.DarkBrown, 1))
            {
                g.DrawLine(linePen, x + w / 3, y + 15, x + w / 3, y + h);
                g.DrawLine(linePen, x + 2 * w / 3, y + 15, x + 2 * w / 3, y + h);
            }

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderTV(Graphics g)
        {
            int x = tv.Bounds.X;
            int y = tv.Bounds.Y;
            int w = tv.Bounds.Width;
            int h = tv.Bounds.Height;

            // TV stand (dark brown)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y + 45, w, h - 45),
                RetroGraphics.Palette.DarkBrown);

            // TV screen bezel (dark gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 6, y, w - 12, 50),
                RetroGraphics.Palette.DarkGray);

            // Screen (black - TV is off)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 10, y + 4, w - 20, 42),
                RetroGraphics.Palette.Black);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderBookshelf(Graphics g)
        {
            int x = bookshelf.Bounds.X;
            int y = bookshelf.Bounds.Y;
            int w = bookshelf.Bounds.Width;
            int h = bookshelf.Bounds.Height;

            // Main body (brown)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Brown);

            // Shelves (darker lines)
            using (Pen shelfPen = new Pen(RetroGraphics.Palette.DarkBrown, 2))
            {
                for (int i = 0; i <= 3; i++)
                {
                    int shelfY = y + (i * h / 3);
                    g.DrawLine(shelfPen, x, shelfY, x + w, shelfY);
                }
            }

            // Books (colored rectangles)
            Color[] bookColors = { RetroGraphics.Palette.Red, RetroGraphics.Palette.Blue,
                                  RetroGraphics.Palette.Green, RetroGraphics.Palette.Yellow };

            Random rand = new Random(42);
            for (int shelf = 0; shelf < 3; shelf++)
            {
                int shelfY = y + 4 + (shelf * h / 3);
                int bookX = x + 4;

                for (int i = 0; i < 6; i++)
                {
                    int bookW = 8 + rand.Next(6);
                    int bookH = 18 + rand.Next(6);
                    Color bookColor = bookColors[rand.Next(bookColors.Length)];

                    RetroGraphics.FillRetroRectangle(g,
                        new Rectangle(bookX, shelfY + (24 - bookH), bookW, bookH),
                        bookColor);

                    bookX += bookW + 1;
                }
            }

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderCoffeeTable(Graphics g)
        {
            int x = coffeeTable.Bounds.X;
            int y = coffeeTable.Bounds.Y;
            int w = coffeeTable.Bounds.Width;
            int h = coffeeTable.Bounds.Height;

            // Table legs (gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 8, y + 10, 4, h - 10),
                RetroGraphics.Palette.DarkGray);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w - 12, y + 10, 4, h - 10),
                RetroGraphics.Palette.DarkGray);

            // Glass top (light cyan with dithering for transparency effect)
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(x, y, w, 12),
                RetroGraphics.Palette.LightCyan,
                RetroGraphics.Palette.White,
                0);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, 12),
                RetroGraphics.Palette.Cyan,
                1);
        }

        private void RenderBook(Graphics g)
        {
            if (!bookTaken)
            {
                int x = book.Bounds.X;
                int y = book.Bounds.Y;
                int w = book.Bounds.Width;
                int h = book.Bounds.Height;

                // Book cover (red)
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(x, y, w, h),
                    RetroGraphics.Palette.Red);

                // Spine
                using (Pen spinePen = new Pen(Color.FromArgb(150, 0, 0), 2))
                {
                    g.DrawLine(spinePen, x + 2, y, x + 2, y + h);
                }

                // Pages (white edge)
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(x + w - 2, y + 1, 2, h - 2),
                    RetroGraphics.Palette.White);
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
                new Rectangle(x + w - 14, y + h / 2 - 3, 6, 6),
                RetroGraphics.Palette.Yellow);

            // Label
            RetroGraphics.DrawRetroText(g, "EXIT", x + 18, y - 15,
                RetroGraphics.Palette.White, 8);
        }
    }
}
