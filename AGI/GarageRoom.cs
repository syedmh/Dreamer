using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;

namespace AGIGame
{
    public class GarageRoom : Room
    {
        private InteractiveObject hallwayDoor = null!;
        private InteractiveObject car = null!;
        private InteractiveObject frontDoor = null!;
        private InteractiveObject toolbox = null!;
        private InteractiveObject note = null!;
        private bool noteRead = false;

        public GarageRoom(int width, int height) : base(width, height)
        {
            InitializeObjects();
        }

        private void InitializeObjects()
        {
            // Door back to hallway
            hallwayDoor = new InteractiveObject(
                "Hallway Door",
                new Rectangle(60, 300, 60, 90),
                280, 390,
                true,
                RenderHallwayDoor,
                (inventory) => "ROOM_TRANSITION:Hallway"
            );
            objects.Add(hallwayDoor);

            // Front door - requires car keys to exit
            frontDoor = new InteractiveObject(
                "Front Door",
                new Rectangle(540, 280, 70, 100),
                260, 380,
                true,
                RenderFrontDoor,
                (inventory) =>
                {
                    if (inventory.Contains("Car Keys"))
                    {
                        return "GAME_WIN";  // Special code for winning
                    }
                    else
                    {
                        return "The front door is locked. You need your car keys to leave!";
                    }
                }
            );
            objects.Add(frontDoor);

            // Car in garage
            car = new InteractiveObject(
                "Car",
                new Rectangle(180, 200, 280, 140),
                180, 340,
                true,
                RenderCar,
                (inventory) =>
                {
                    if (inventory.Contains("Car Keys"))
                    {
                        return "Your trusty car is ready to go. Time to leave through the front door!";
                    }
                    else
                    {
                        return "Your car is here, but where are the keys? Check the kitchen.";
                    }
                }
            );
            objects.Add(car);

            // Toolbox
            toolbox = new InteractiveObject(
                "Toolbox",
                new Rectangle(500, 180, 60, 40),
                160, 220,
                true,
                RenderToolbox,
                (inventory) => "A red metal toolbox filled with wrenches, screwdrivers, and other tools."
            );
            objects.Add(toolbox);

            // Note on wall
            note = new InteractiveObject(
                "Note",
                new Rectangle(300, 80, 30, 20),
                75, 95,
                false,
                RenderNote,
                (inventory) =>
                {
                    noteRead = true;
                    return "Note: 'Remember - car keys are in the kitchen!'";
                }
            );
            objects.Add(note);
        }

        public override void Render(Graphics g)
        {
            RetroGraphics.SetupRetroGraphics(g);

            // Back wall (gray concrete)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(0, 0, Width, Height / 3),
                RetroGraphics.Palette.DarkGray);

            // Floor (gray concrete with dithering)
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(0, Height / 3, Width, Height * 2 / 3),
                RetroGraphics.Palette.DarkGray,
                RetroGraphics.Palette.LightGray,
                3);

            // Oil stain on floor
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(220, 320, 40, 20),
                RetroGraphics.Palette.Black);

            // Base line
            using (Pen basePen = new Pen(RetroGraphics.Palette.Black, 2))
            {
                g.DrawLine(basePen, 0, Height / 3, Width, Height / 3);
            }
        }

        private void RenderCar(Graphics g)
        {
            int x = car.Bounds.X;
            int y = car.Bounds.Y;
            int w = car.Bounds.Width;
            int h = car.Bounds.Height;

            // Car body (red)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 20, y + 20, w - 40, h - 50),
                RetroGraphics.Palette.Red);

            // Car roof
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 60, y, w - 120, 25),
                RetroGraphics.Palette.Red);

            // Windows (cyan)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 65, y + 3, 50, 18),
                RetroGraphics.Palette.Cyan);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w - 115, y + 3, 50, 18),
                RetroGraphics.Palette.Cyan);

            // Wheels (black)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + 40, y + h - 35, 30, 30),
                RetroGraphics.Palette.Black);
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + w - 70, y + h - 35, 30, 30),
                RetroGraphics.Palette.Black);

            // Hubcaps (gray)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + 50, y + h - 25, 10, 10),
                RetroGraphics.Palette.LightGray);
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(x + w - 60, y + h - 25, 10, 10),
                RetroGraphics.Palette.LightGray);

            // Headlights (yellow)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 10, y + 30, 12, 8),
                RetroGraphics.Palette.Yellow);
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 10, y + 50, 12, 8),
                RetroGraphics.Palette.Yellow);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 20, y + 20, w - 40, h - 50),
                RetroGraphics.Palette.Black,
                2);
        }

        private void RenderToolbox(Graphics g)
        {
            int x = toolbox.Bounds.X;
            int y = toolbox.Bounds.Y;
            int w = toolbox.Bounds.Width;
            int h = toolbox.Bounds.Height;

            // Toolbox body (red)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Red);

            // Handle (gray)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w / 2 - 8, y - 6, 16, 8),
                RetroGraphics.Palette.DarkGray);

            // Latch (yellow)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + w / 2 - 4, y + h / 2 - 2, 8, 4),
                RetroGraphics.Palette.Yellow);

            // Outline
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Black,
                1);
        }

        private void RenderNote(Graphics g)
        {
            if (!noteRead)
            {
                int x = note.Bounds.X;
                int y = note.Bounds.Y;
                int w = note.Bounds.Width;
                int h = note.Bounds.Height;

                // Note paper (white)
                RetroGraphics.FillRetroRectangle(g,
                    new Rectangle(x, y, w, h),
                    RetroGraphics.Palette.White);

                // Text lines (black)
                using (Pen textPen = new Pen(RetroGraphics.Palette.Black, 1))
                {
                    for (int i = 0; i < 4; i++)
                    {
                        g.DrawLine(textPen, x + 2, y + 4 + (i * 4), x + w - 2, y + 4 + (i * 4));
                    }
                }

                // Border
                RetroGraphics.DrawRetroRectangle(g,
                    new Rectangle(x, y, w, h),
                    RetroGraphics.Palette.Black,
                    1);
            }
        }

        private void RenderFrontDoor(Graphics g)
        {
            int x = frontDoor.Bounds.X;
            int y = frontDoor.Bounds.Y;
            int w = frontDoor.Bounds.Width;
            int h = frontDoor.Bounds.Height;

            // Door frame
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x - 4, y - 4, w + 8, h + 8),
                RetroGraphics.Palette.DarkBrown);

            // Door (brown)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Brown);

            // Door panels
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 6, y + 8, w - 12, h / 2 - 12),
                RetroGraphics.Palette.DarkBrown,
                2);
            RetroGraphics.DrawRetroRectangle(g,
                new Rectangle(x + 6, y + h / 2 + 4, w - 12, h / 2 - 12),
                RetroGraphics.Palette.DarkBrown,
                2);

            // Door handle with keyhole
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 10, y + h / 2 - 4, 8, 8),
                RetroGraphics.Palette.Yellow);

            // Keyhole (black)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x + 12, y + h / 2, 4, 4),
                RetroGraphics.Palette.Black);

            // Label
            RetroGraphics.DrawRetroText(g, "FRONT DOOR", x + 2, y - 15,
                RetroGraphics.Palette.White, 7);
        }

        private void RenderHallwayDoor(Graphics g)
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
            RetroGraphics.DrawRetroText(g, "HALLWAY", x + 6, y - 15,
                RetroGraphics.Palette.White, 7);
        }
    }
}
