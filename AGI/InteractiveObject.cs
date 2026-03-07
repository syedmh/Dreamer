using System;
using System.Drawing;
using System.Collections.Generic;

namespace AGIGame
{
    public class InteractiveObject
    {
        public Rectangle Bounds { get; set; }
        public bool IsSolid { get; set; }
        public bool CanInteract { get; set; }
        public string Name { get; set; }
        public int DepthMin { get; set; }  // Minimum Y for collision
        public int DepthMax { get; set; }  // Maximum Y for collision
        public int DepthCenter => (DepthMin + DepthMax) / 2;

        private Action<Graphics> renderAction;
        private Func<List<string>, string>? interactAction;
        private bool hasBeenInteracted;

        public InteractiveObject(string name, Rectangle bounds, int depthMin, int depthMax,
            bool isSolid, Action<Graphics> render, Func<List<string>, string>? interact = null)
        {
            Name = name;
            Bounds = bounds;
            DepthMin = depthMin;
            DepthMax = depthMax;
            IsSolid = isSolid;
            CanInteract = interact != null;
            renderAction = render;
            interactAction = interact;
            hasBeenInteracted = false;
        }

        public void Render(Graphics g)
        {
            renderAction(g);
        }

        public string? Interact(List<string> inventory)
        {
            if (interactAction != null && CanInteract)
            {
                string result = interactAction(inventory);
                hasBeenInteracted = true;
                return result;
            }
            return null;
        }

        public bool HasBeenInteracted => hasBeenInteracted;
    }
}
