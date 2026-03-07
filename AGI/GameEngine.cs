using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;

namespace AGIGame
{
    public class GameEngine
    {
        private int screenWidth;
        private int screenHeight;
        private Player player;
        private Room? currentRoom;
        private HashSet<Keys> pressedKeys;
        private string message;
        private int messageTimer;
        private List<string> inventory;
        private int score;
        private int maxScore = 100;  // Total possible points
        private HashSet<string> discoveredObjects;  // Track what's been examined for points

        public GameEngine(int width, int height)
        {
            screenWidth = width;
            screenHeight = height;
            pressedKeys = new HashSet<Keys>();
            message = "Use arrow keys to move. Press SPACE to interact, or type commands below.";
            messageTimer = 180; // Show for 3 seconds at 60 FPS
            inventory = new List<string>();
            score = 0;
            discoveredObjects = new HashSet<string>();

            // Create player
            player = new Player(320, 300);

            // Load the bedroom
            currentRoom = new BedroomRoom(screenWidth, screenHeight);
        }

        public void Update()
        {
            if (currentRoom != null)
            {
                // Handle player movement based on pressed keys
                int dx = 0, dy = 0;
                if (pressedKeys.Contains(Keys.Left)) dx -= 2;
                if (pressedKeys.Contains(Keys.Right)) dx += 2;
                if (pressedKeys.Contains(Keys.Up)) dy -= 2;
                if (pressedKeys.Contains(Keys.Down)) dy += 2;

                player.Move(dx, dy, currentRoom);

                // Check for interactions
                if (pressedKeys.Contains(Keys.Space))
                {
                    int prevInventoryCount = inventory.Count;
                    string? interactionResult = currentRoom.TryInteract(player, inventory);
                    if (interactionResult != null)
                    {
                        // Check if it's a game win
                        if (interactionResult == "GAME_WIN")
                        {
                            HandleGameWin();
                        }
                        // Check if it's a room transition
                        else if (interactionResult.StartsWith("ROOM_TRANSITION:"))
                        {
                            string roomName = interactionResult.Substring("ROOM_TRANSITION:".Length);
                            TransitionToRoom(roomName);
                        }
                        else
                        {
                            message = interactionResult;
                            messageTimer = 180;

                            // Award points if an item was added
                            if (inventory.Count > prevInventoryCount)
                            {
                                AwardPoints(10, "finding an item");
                            }
                        }
                    }
                    pressedKeys.Remove(Keys.Space); // Prevent repeated interactions
                }
            }

            // Update message timer
            if (messageTimer > 0)
                messageTimer--;
        }

        public void Render(Graphics g)
        {
            // Clear screen
            g.Clear(Color.Black);

            if (currentRoom != null)
            {
                // Render current room background
                currentRoom.Render(g);

                // Render objects and player with proper depth sorting
                currentRoom.RenderObjectsWithDepth(g, player);
            }

            // Render UI
            RenderUI(g);
        }

        private void RenderUI(Graphics g)
        {
            // Draw message box at bottom with retro style
            Rectangle messageBox = new Rectangle(10, screenHeight - 80, screenWidth - 20, 70);

            // Black background for message box
            RetroGraphics.FillRetroRectangle(g, messageBox, RetroGraphics.Palette.Black);

            // White border
            RetroGraphics.DrawRetroRectangle(g, messageBox, RetroGraphics.Palette.White, 2);

            // Draw message text with retro font
            if (messageTimer > 0 || message.StartsWith("Use arrow"))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixel;
                using (Font font = new Font("Courier New", 10, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(RetroGraphics.Palette.White))
                {
                    g.DrawString(message, font, brush,
                        new RectangleF(15, screenHeight - 75, screenWidth - 30, 60));
                }
            }

            // Draw inventory with retro style
            if (inventory.Count > 0)
            {
                string invText = "Inventory: " + string.Join(", ", inventory);
                RetroGraphics.DrawRetroText(g, invText, 15, 10, RetroGraphics.Palette.Yellow, 9);
            }

            // Draw score in top right corner
            string scoreText = $"Score: {score}/{maxScore}";
            RetroGraphics.DrawRetroText(g, scoreText, screenWidth - 130, 10, RetroGraphics.Palette.White, 9);
        }

        public void HandleKeyDown(Keys key)
        {
            pressedKeys.Add(key);
        }

        public void HandleKeyUp(Keys key)
        {
            pressedKeys.Remove(key);
        }

        public void ProcessTextCommand(string input)
        {
            ParsedCommand cmd = TextParser.Parse(input);

            if (!cmd.IsValid)
            {
                message = cmd.Message;
                messageTimer = 180;
                return;
            }

            if (currentRoom == null)
            {
                message = "You are nowhere.";
                messageTimer = 180;
                return;
            }

            // Handle commands based on type
            switch (cmd.Type)
            {
                case CommandType.Look:
                    HandleLookCommand(cmd);
                    break;

                case CommandType.Take:
                    HandleTakeCommand(cmd);
                    break;

                case CommandType.Use:
                    HandleUseCommand(cmd);
                    break;

                case CommandType.Go:
                    HandleGoCommand(cmd);
                    break;

                default:
                    message = "I don't know how to do that.";
                    messageTimer = 180;
                    break;
            }
        }

        private void HandleLookCommand(ParsedCommand cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd.Target))
            {
                message = "You are in " + currentRoom?.GetType().Name.Replace("Room", "") + ". " +
                         "You can use arrow keys to move and SPACE to interact, or type commands.";
                messageTimer = 240;
                return;
            }

            // Find matching object
            var matchingObject = FindMatchingObject(cmd.Target);
            if (matchingObject != null)
            {
                string? result = matchingObject.Interact(inventory);
                message = result ?? "You see nothing special.";
                messageTimer = 180;

                // Award points for examining objects (first time only)
                string objectKey = $"looked_{matchingObject.Name}";
                if (!discoveredObjects.Contains(objectKey))
                {
                    discoveredObjects.Add(objectKey);
                    AwardPoints(2, "examining");
                }
            }
            else
            {
                message = $"You don't see any '{cmd.Target}' here.";
                messageTimer = 180;
            }
        }

        private void HandleTakeCommand(ParsedCommand cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd.Target))
            {
                message = "Take what?";
                messageTimer = 120;
                return;
            }

            var matchingObject = FindMatchingObject(cmd.Target);
            if (matchingObject != null && matchingObject.CanInteract)
            {
                int prevInventoryCount = inventory.Count;
                string? result = matchingObject.Interact(inventory);
                message = result ?? "You can't take that.";
                messageTimer = 180;

                // Award points if an item was added to inventory
                if (inventory.Count > prevInventoryCount)
                {
                    AwardPoints(10, "finding an item");
                }
            }
            else
            {
                message = $"You don't see any '{cmd.Target}' here.";
                messageTimer = 180;
            }
        }

        private void HandleUseCommand(ParsedCommand cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd.Target))
            {
                message = "Use what?";
                messageTimer = 120;
                return;
            }

            var matchingObject = FindMatchingObject(cmd.Target);
            if (matchingObject != null)
            {
                string? result = matchingObject.Interact(inventory);

                // Check if it's a game win
                if (result == "GAME_WIN")
                {
                    HandleGameWin();
                }
                // Check for room transition
                else if (result != null && result.StartsWith("ROOM_TRANSITION:"))
                {
                    string roomName = result.Substring("ROOM_TRANSITION:".Length);
                    TransitionToRoom(roomName);
                }
                else
                {
                    message = result ?? "Nothing happens.";
                    messageTimer = 180;
                }
            }
            else
            {
                message = $"You don't see any '{cmd.Target}' here.";
                messageTimer = 180;
            }
        }

        private void HandleGoCommand(ParsedCommand cmd)
        {
            // Find door objects
            var doorObject = FindMatchingObject("door");
            if (doorObject != null)
            {
                string? result = doorObject.Interact(inventory);
                if (result != null && result.StartsWith("ROOM_TRANSITION:"))
                {
                    string roomName = result.Substring("ROOM_TRANSITION:".Length);
                    TransitionToRoom(roomName);
                }
            }
            else
            {
                message = "Go where? Try using a door.";
                messageTimer = 180;
            }
        }

        private InteractiveObject? FindMatchingObject(string target)
        {
            if (currentRoom == null) return null;

            // Get all objects in current room
            var roomObjects = typeof(Room)
                .GetField("objects", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(currentRoom) as System.Collections.Generic.List<InteractiveObject>;

            if (roomObjects == null) return null;

            // Find object that matches the target
            foreach (var obj in roomObjects)
            {
                if (TextParser.MatchesObject(target, obj.Name))
                {
                    return obj;
                }
            }

            return null;
        }

        private void TransitionToRoom(string roomName)
        {
            string? currentRoomName = currentRoom?.GetType().Name.Replace("Room", "");

            switch (roomName)
            {
                case "Bedroom":
                    currentRoom = new BedroomRoom(screenWidth, screenHeight);
                    // Position player near the door they came from
                    if (currentRoomName == "Hallway")
                    {
                        player = new Player(560, 340); // Near the exit door
                    }
                    else
                    {
                        player = new Player(320, 300); // Default position
                    }
                    message = "You enter the bedroom.";
                    messageTimer = 120;
                    break;

                case "Hallway":
                    currentRoom = new HallwayRoom(screenWidth, screenHeight);
                    // Position player near the door they came from
                    if (currentRoomName == "Bedroom")
                    {
                        player = new Player(100, 200); // Near the bedroom door
                    }
                    else if (currentRoomName == "Kitchen")
                    {
                        player = new Player(190, 140); // Near the kitchen door
                    }
                    else if (currentRoomName == "LivingRoom")
                    {
                        player = new Player(560, 300); // Near the living room door
                    }
                    else if (currentRoomName == "Bathroom")
                    {
                        player = new Player(450, 140); // Near the bathroom door
                    }
                    else
                    {
                        player = new Player(320, 300); // Default position
                    }
                    message = "You step into the hallway.";
                    messageTimer = 120;
                    break;

                case "Kitchen":
                    currentRoom = new KitchenRoom(screenWidth, screenHeight);
                    // Position player near the door
                    if (currentRoomName == "Hallway")
                    {
                        player = new Player(560, 340); // Near the exit door
                    }
                    else
                    {
                        player = new Player(320, 300); // Default position
                    }
                    message = "You enter the kitchen. The smell of coffee lingers in the air.";
                    messageTimer = 120;
                    break;

                case "LivingRoom":
                    currentRoom = new LivingRoom(screenWidth, screenHeight);
                    // Position player near the door
                    if (currentRoomName == "Hallway")
                    {
                        player = new Player(90, 340); // Near the exit door
                    }
                    else
                    {
                        player = new Player(320, 300); // Default position
                    }
                    message = "You enter the living room. It's cozy and inviting.";
                    messageTimer = 120;
                    break;

                case "Bathroom":
                    currentRoom = new BathroomRoom(screenWidth, screenHeight);
                    // Position player near the door
                    if (currentRoomName == "Hallway")
                    {
                        player = new Player(560, 340); // Near the exit door
                    }
                    else
                    {
                        player = new Player(320, 300); // Default position
                    }
                    message = "You enter the bathroom. It's clean and well-maintained.";
                    messageTimer = 120;
                    break;

                case "Garage":
                    currentRoom = new GarageRoom(screenWidth, screenHeight);
                    // Position player near the door
                    if (currentRoomName == "Hallway")
                    {
                        player = new Player(100, 340); // Near the hallway door
                    }
                    else
                    {
                        player = new Player(320, 300); // Default position
                    }
                    message = "You enter the garage. Your car is here. Find the front door to leave!";
                    messageTimer = 120;
                    break;

                default:
                    message = $"Room '{roomName}' not found!";
                    messageTimer = 120;
                    break;
            }

            // Award points for entering a new room (first time only)
            string roomKey = $"entered_{roomName}";
            if (!discoveredObjects.Contains(roomKey))
            {
                discoveredObjects.Add(roomKey);
                AwardPoints(5, "exploring");
            }
        }

        private void AwardPoints(int points, string reason)
        {
            score += points;
            if (score > maxScore)
                score = maxScore;

            message = $"+{points} points for {reason}! Score: {score}/{maxScore}";
            messageTimer = 180;
        }

        private void HandleGameWin()
        {
            // Award final points for escaping
            AwardPoints(50, "escaping the house");

            message = $"*** YOU WIN! *** You escaped with {score}/{maxScore} points! " +
                     "Close the window to exit or keep exploring.";
            messageTimer = 600; // Show for 10 seconds
        }
    }
}
