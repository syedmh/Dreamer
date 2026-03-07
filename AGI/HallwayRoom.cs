using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;

namespace AGIGame
{
    public class HallwayRoom : Room
    {
        private InteractiveObject bedroomDoor = null!;
        private InteractiveObject kitchenDoor = null!;
        private InteractiveObject livingRoomDoor = null!;
        private InteractiveObject bathroomDoor = null!;
        private InteractiveObject garageDoor = null!;
        private InteractiveObject picture = null!;
        private InteractiveObject table = null!;
        private InteractiveObject plant = null!;

        public HallwayRoom(int width, int height) : base(width, height)
        {
            InitializeObjects();
        }

        private void InitializeObjects()
        {
            // Bedroom door (left side)
            bedroomDoor = new InteractiveObject(
                "Bedroom Door",
                new Rectangle(60, 140, 50, 80),
                140, 220,
                true,
                RenderBedroomDoor,
                (inventory) => "ROOM_TRANSITION:Bedroom"
            );
            objects.Add(bedroomDoor);

            // Kitchen door (back wall, left)
            kitchenDoor = new InteractiveObject(
                "Kitchen Door",
                new Rectangle(160, 90, 50, 70),
                90, 160,
                true,
                RenderKitchenDoor,
                (inventory) => "ROOM_TRANSITION:Kitchen"
            );
            objects.Add(kitchenDoor);

            // Living room door (right side, front)
            livingRoomDoor = new InteractiveObject(
                "Living Room Door",
                new Rectangle(540, 260, 50, 80),
                260, 340,
                true,
                RenderLivingRoomDoor,
                (inventory) => "ROOM_TRANSITION:LivingRoom"
            );
            objects.Add(livingRoomDoor);

            // Bathroom door (back wall, right)
            bathroomDoor = new InteractiveObject(
                "Bathroom Door",
                new Rectangle(420, 95, 50, 65),
                95, 160,
                true,
                RenderBathroomDoor,
                (inventory) => "ROOM_TRANSITION:Bathroom"
            );
            objects.Add(bathroomDoor);

            // Garage door (bottom right)
            garageDoor = new InteractiveObject(
                "Garage Door",
                new Rectangle(530, 320, 50, 75),
                320, 395,
                true,
                RenderGarageDoor,
                (inventory) => "ROOM_TRANSITION:Garage"
            );
            objects.Add(garageDoor);

            // Decorative picture on wall
            picture = new InteractiveObject(
                "Picture",
                new Rectangle(280, 60, 60, 50),
                50, 80,
                false,
                RenderPicture,
                (inventory) => "A beautiful landscape painting of mountains at sunset."
            );
            objects.Add(picture);

            // Small table with lamp (right side)
            table = new InteractiveObject(
                "Table",
                new Rectangle(500, 200, 80, 60),
                180, 260,
                true,
                RenderTable,
                (inventory) => "A small hallway table with a decorative lamp on it."
            );
            objects.Add(table);

            // Potted plant
            plant = new InteractiveObject(
                "Plant",
                new Rectangle(450, 280, 40, 50),
                260, 330,
                true,
                RenderPlant,
                (inventory) => "A healthy potted fern. It looks well-maintained."
            );
            objects.Add(plant);
        }

        public override void Render(Graphics g)
        {
            RetroGraphics.SetupRetroGraphics(g);

            // Draw back wall (beige)
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(0, 0, Width, Height / 3),
                RetroGraphics.Palette.Tan);

            // Draw floor with dithering
            RetroGraphics.FillDitheredRectangle(g,
                new Rectangle(0, Height / 3, Width, Height * 2 / 3),
                RetroGraphics.Palette.Brown,
                RetroGraphics.Palette.DarkBrown,
                1);

            // Draw baseboards
            using (Pen basePen = new Pen(RetroGraphics.Palette.DarkBrown, 2))
            {
                g.DrawLine(basePen, 0, Height / 3, Width, Height / 3);
            }

            // Draw runner rug
            DrawRunnerRug(g, 150, 280, 340, 100);

            // Draw light (simple yellow circle)
            RetroGraphics.FillRetroEllipse(g,
                new Rectangle(Width / 2 - 10, 30, 20, 20),
                RetroGraphics.Palette.Yellow);
        }

        private void DrawFloorWithPerspective(Graphics g)
        {
            // Not used in retro mode
        }

        private void DrawLightFixture(Graphics g, int x, int y)
        {
            // Light glow
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(x - 30, y - 15, 60, 30);
                using (PathGradientBrush glowBrush = new PathGradientBrush(path))
                {
                    glowBrush.CenterColor = Color.FromArgb(100, 255, 255, 200);
                    glowBrush.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 200) };
                    g.FillPath(glowBrush, path);
                }
            }

            // Fixture base
            using (SolidBrush fixtureBrush = new SolidBrush(Color.FromArgb(180, 180, 180)))
            {
                g.FillEllipse(fixtureBrush, x - 15, y - 8, 30, 16);
            }
            using (Pen fixturePen = new Pen(Color.FromArgb(140, 140, 140), 2))
            {
                g.DrawEllipse(fixturePen, x - 15, y - 8, 30, 16);
            }
        }

        private void DrawRunnerRug(Graphics g, int x, int y, int w, int h)
        {
            // Rug base - red
            RetroGraphics.FillRetroRectangle(g,
                new Rectangle(x, y, w, h),
                RetroGraphics.Palette.Red);

            // Decorative borders (dark red)
            using (SolidBrush borderBrush = new SolidBrush(Color.FromArgb(120, 0, 0)))
            {
                g.FillRectangle(borderBrush, x, y, w, 4);
                g.FillRectangle(borderBrush, x, y + h - 4, w, 4);
                g.FillRectangle(borderBrush, x, y, 4, h);
                g.FillRectangle(borderBrush, x + w - 4, y, 4, h);
            }

            // Pattern stripes
            using (Pen stripePen = new Pen(RetroGraphics.Palette.Yellow, 1))
            {
                g.DrawLine(stripePen, x + w / 2, y + 6, x + w / 2, y + h - 6);
            }
        }

        private void RenderBedroomDoor(Graphics g)
        {
            int x = bedroomDoor.Bounds.X;
            int y = bedroomDoor.Bounds.Y;
            int w = bedroomDoor.Bounds.Width;
            int h = bedroomDoor.Bounds.Height;

            RenderDoorGeneric(g, x, y, w, h, "BEDROOM");
        }

        private void RenderPicture(Graphics g)
        {
            int x = picture.Bounds.X;
            int y = picture.Bounds.Y;
            int w = picture.Bounds.Width;
            int h = picture.Bounds.Height;

            // Frame shadow
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            {
                g.FillRectangle(shadowBrush, x + 2, y + 2, w, h);
            }

            // Frame
            using (SolidBrush frameBrush = new SolidBrush(Color.FromArgb(40, 30, 20)))
            {
                g.FillRectangle(frameBrush, x - 3, y - 3, w + 6, h + 6);
            }

            // Picture - simple landscape
            using (LinearGradientBrush skyBrush = new LinearGradientBrush(
                new Rectangle(x, y, w, h / 2),
                Color.FromArgb(255, 180, 120),
                Color.FromArgb(180, 150, 200),
                90f))
            {
                g.FillRectangle(skyBrush, x, y, w, h / 2);
            }

            // Mountains
            Point[] mountains = {
                new Point(x, y + h / 2),
                new Point(x + w / 3, y + h / 4),
                new Point(x + w * 2 / 3, y + h / 3),
                new Point(x + w, y + h / 2)
            };
            using (SolidBrush mountainBrush = new SolidBrush(Color.FromArgb(80, 60, 100)))
            {
                g.FillPolygon(mountainBrush, mountains);
            }

            // Ground
            using (SolidBrush groundBrush = new SolidBrush(Color.FromArgb(60, 100, 60)))
            {
                g.FillRectangle(groundBrush, x, y + h / 2, w, h / 2);
            }
        }

        private void RenderTable(Graphics g)
        {
            int x = table.Bounds.X;
            int y = table.Bounds.Y;
            int w = table.Bounds.Width;
            int h = table.Bounds.Height;

            // Shadow
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            {
                g.FillRectangle(shadowBrush, x + 4, y + 4, w, h);
            }

            // Table legs
            using (SolidBrush legBrush = new SolidBrush(Color.FromArgb(70, 50, 30)))
            {
                g.FillRectangle(legBrush, x + 5, y + 15, 6, h - 15);
                g.FillRectangle(legBrush, x + w - 11, y + 15, 6, h - 15);
                g.FillRectangle(legBrush, x + 5, y + 15, 6, h - 15);
                g.FillRectangle(legBrush, x + w - 11, y + 15, 6, h - 15);
            }

            // Table top (3D perspective)
            Point[] top = {
                new Point(x, y),
                new Point(x + w, y),
                new Point(x + w - 8, y + 15),
                new Point(x + 8, y + 15)
            };
            using (LinearGradientBrush topBrush = new LinearGradientBrush(
                new Rectangle(x, y, w, 15),
                Color.FromArgb(120, 90, 60),
                Color.FromArgb(90, 70, 45),
                180f))
            {
                g.FillPolygon(topBrush, top);
            }

            // Lamp on table
            DrawLamp(g, x + w / 2, y - 20);
        }

        private void DrawLamp(Graphics g, int x, int y)
        {
            // Lamp glow
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(x - 20, y - 10, 40, 25);
                using (PathGradientBrush glowBrush = new PathGradientBrush(path))
                {
                    glowBrush.CenterColor = Color.FromArgb(80, 255, 240, 200);
                    glowBrush.SurroundColors = new[] { Color.FromArgb(0, 255, 240, 200) };
                    g.FillPath(glowBrush, path);
                }
            }

            // Lampshade
            Point[] shade = {
                new Point(x - 12, y),
                new Point(x + 12, y),
                new Point(x + 15, y + 12),
                new Point(x - 15, y + 12)
            };
            using (SolidBrush shadeBrush = new SolidBrush(Color.FromArgb(220, 200, 150)))
            {
                g.FillPolygon(shadeBrush, shade);
            }
            using (Pen shadePen = new Pen(Color.FromArgb(180, 160, 120), 1))
            {
                g.DrawPolygon(shadePen, shade);
            }

            // Base
            using (SolidBrush baseBrush = new SolidBrush(Color.FromArgb(60, 50, 40)))
            {
                g.FillRectangle(baseBrush, x - 3, y + 12, 6, 8);
                g.FillEllipse(baseBrush, x - 8, y + 20, 16, 6);
            }
        }

        private void RenderPlant(Graphics g)
        {
            int x = plant.Bounds.X;
            int y = plant.Bounds.Y;
            int w = plant.Bounds.Width;
            int h = plant.Bounds.Height;

            // Shadow
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            {
                g.FillEllipse(shadowBrush, x + 3, y + h - 8, w, 12);
            }

            // Pot
            using (LinearGradientBrush potBrush = new LinearGradientBrush(
                new Rectangle(x + 5, y + h - 20, w - 10, 20),
                Color.FromArgb(180, 100, 60),
                Color.FromArgb(140, 80, 50),
                90f))
            {
                Point[] pot = {
                    new Point(x + 8, y + h - 20),
                    new Point(x + w - 8, y + h - 20),
                    new Point(x + w - 5, y + h),
                    new Point(x + 5, y + h)
                };
                g.FillPolygon(potBrush, pot);
            }

            // Soil
            using (SolidBrush soilBrush = new SolidBrush(Color.FromArgb(60, 40, 20)))
            {
                g.FillEllipse(soilBrush, x + 8, y + h - 22, w - 16, 8);
            }

            // Fern leaves
            using (SolidBrush leafBrush = new SolidBrush(Color.FromArgb(50, 120, 50)))
            {
                for (int i = 0; i < 8; i++)
                {
                    double angle = (i * Math.PI / 4) - Math.PI / 2;
                    int leafLength = 15 + (i % 2) * 5;
                    int tipX = x + w / 2 + (int)(Math.Cos(angle) * leafLength);
                    int tipY = y + h - 22 + (int)(Math.Sin(angle) * leafLength);

                    Point[] leaf = {
                        new Point(x + w / 2, y + h - 22),
                        new Point(tipX - 3, tipY),
                        new Point(tipX + 3, tipY)
                    };
                    g.FillPolygon(leafBrush, leaf);
                }
            }
        }

        private void RenderKitchenDoor(Graphics g)
        {
            int x = kitchenDoor.Bounds.X;
            int y = kitchenDoor.Bounds.Y;
            int w = kitchenDoor.Bounds.Width;
            int h = kitchenDoor.Bounds.Height;

            RenderDoorGeneric(g, x, y, w, h, "KITCHEN");
        }

        private void RenderLivingRoomDoor(Graphics g)
        {
            int x = livingRoomDoor.Bounds.X;
            int y = livingRoomDoor.Bounds.Y;
            int w = livingRoomDoor.Bounds.Width;
            int h = livingRoomDoor.Bounds.Height;

            RenderDoorGeneric(g, x, y, w, h, "LIVING ROOM");
        }

        private void RenderBathroomDoor(Graphics g)
        {
            int x = bathroomDoor.Bounds.X;
            int y = bathroomDoor.Bounds.Y;
            int w = bathroomDoor.Bounds.Width;
            int h = bathroomDoor.Bounds.Height;

            RenderDoorGeneric(g, x, y, w, h, "BATHROOM");
        }

        private void RenderGarageDoor(Graphics g)
        {
            int x = garageDoor.Bounds.X;
            int y = garageDoor.Bounds.Y;
            int w = garageDoor.Bounds.Width;
            int h = garageDoor.Bounds.Height;

            RenderDoorGeneric(g, x, y, w, h, "GARAGE");
        }

        private void RenderDoorGeneric(Graphics g, int x, int y, int w, int h, string label)
        {
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
                new Rectangle(x + w - 14, y + h / 2 - 3, 6, 6),
                RetroGraphics.Palette.Yellow);

            // Door label
            int labelX = x + (w / 2) - (label.Length * 3);
            RetroGraphics.DrawRetroText(g, label, labelX, y - 13,
                RetroGraphics.Palette.White, 6);
        }
    }
}
