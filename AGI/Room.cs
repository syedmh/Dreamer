using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;

namespace AGIGame
{
    public abstract class Room
    {
        public int Width { get; protected set; }
        public int Height { get; protected set; }
        protected List<InteractiveObject> objects;

        public Room(int width, int height)
        {
            Width = width;
            Height = height - 80; // Reserve space for UI at bottom
            objects = new List<InteractiveObject>();
        }

        public abstract void Render(Graphics g);

        public bool CheckCollision(Rectangle playerRect, int playerDepth)
        {
            foreach (var obj in objects)
            {
                if (obj.IsSolid && obj.Bounds.IntersectsWith(playerRect))
                {
                    // Check if player is at the right depth to collide
                    if (playerDepth >= obj.DepthMin && playerDepth <= obj.DepthMax)
                        return true;
                }
            }
            return false;
        }

        public string? TryInteract(Player player, List<string> inventory)
        {
            Rectangle interactionArea = player.GetInteractionArea();

            // Sort objects by depth, prioritize those closest to player
            var sortedObjects = objects
                .Where(obj => obj.CanInteract && obj.Bounds.IntersectsWith(interactionArea))
                .OrderBy(obj => Math.Abs(obj.DepthCenter - player.GetDepth()))
                .ToList();

            if (sortedObjects.Any())
            {
                return sortedObjects.First().Interact(inventory);
            }

            return null;
        }

        // Render objects with z-ordering based on depth
        public void RenderObjectsWithDepth(Graphics g, Player player)
        {
            // Combine objects and player into a list for depth sorting
            var renderList = new List<(int depth, Action<Graphics> render)>();

            foreach (var obj in objects)
            {
                int objDepth = obj.DepthCenter;
                renderList.Add((objDepth, obj.Render));
            }

            // Add player to render list
            renderList.Add((player.GetDepth(), player.Render));

            // Sort by depth (back to front)
            renderList = renderList.OrderBy(item => item.depth).ToList();

            // Render in order
            foreach (var item in renderList)
            {
                item.render(g);
            }
        }
    }
}
