# AGI Adventure Game - Retro King's Quest Style

A classic Sierra Online-style adventure game built in C# with authentic retro EGA graphics, just like King's Quest from the 1980s!

## Features

### Retro Pixelated Graphics
- **Authentic EGA 16-Color Palette** - Classic colors from the Sierra AGI/SCI engine
- **No Anti-Aliasing** - Sharp, blocky pixels for that retro feel
- **Dithering Effects** - Checkerboard patterns replace smooth gradients
- **Depth-Based Scaling** - Character shrinks when walking to the back of rooms
- **Z-Ordering** - Objects render correctly based on depth (back to front)

### Complete House to Explore
**6 Fully Detailed Rooms**:
1. **Bedroom** - Start here! Pick up your wallet from the dresser
2. **Hallway** - Central hub connecting all rooms
3. **Kitchen** - Find the car keys and an apple
4. **Living Room** - Grab a mystery novel from the coffee table
5. **Bathroom** - Take a towel
6. **Garage** - Your car awaits! Use the front door to escape

### Dual Control System
**Classic Arrow Key Movement**:
- Arrow keys to move character
- SPACE to interact with nearby objects
- Character has 4-way directional sprites with walking animation

**Text Parser Commands** (Sierra-style):
- Type commands in the text box at the bottom
- Supported verbs: LOOK, TAKE, USE, GO, EXAMINE, OPEN
- Examples:
  - `look at bed`
  - `take wallet`
  - `use door`
  - `examine car`
  - `go door`

### Score System
- **100 points** total possible
- **+2 points** for examining objects (first time)
- **+5 points** for entering new rooms (first time)
- **+10 points** for finding collectible items
- **+50 points** for escaping the house!
- Score displayed in top-right corner

### Win Condition
**Goal**: Escape the house!
1. Find the **Car Keys** in the kitchen
2. Go to the **Garage**
3. Use the **Front Door** with your car keys
4. WIN!

### Interactive Objects
Over 20 interactive objects including:
- Furniture: Bed, Sofa, Tables, Chairs
- Appliances: Refrigerator, Stove, Sink, TV
- Decorations: Pictures, Plants, Rugs
- Bathroom Fixtures: Toilet, Shower, Mirror
- Garage Items: Car, Toolbox

### Collectible Items (5 total)
1. **Wallet** - On dresser in bedroom
2. **Apple** - On counter in kitchen
3. **Car Keys** - On counter in kitchen (needed to win!)
4. **Mystery Novel** - On coffee table in living room
5. **Towel** - In bathroom

## Controls

### Movement
- **Arrow Keys**: Move character (Up, Down, Left, Right)
- **SPACE**: Interact with nearby objects

### Text Commands
Type in the text box at bottom and press ENTER:
- `look` or `look at [object]` - Examine things
- `take [object]` or `get [object]` - Pick up items
- `use [object]` or `open [object]` - Interact with objects
- `go [direction]` or `enter door` - Move through doors

## How to Run

```bash
dotnet run
```

## How to Build

```bash
dotnet build
```

## Technical Details

### Architecture
- **GameEngine**: Main game logic, scoring, and command processing
- **TextParser**: Parses natural language commands
- **Room System**: Abstract base class for easy room creation
- **InteractiveObject**: Generic system for any interactive item
- **Player**: Character with movement, animation, and depth scaling
- **RetroGraphics**: Helper class for pixelated rendering

### Key Classes
- `GameEngine.cs` - Game loop, state management, scoring
- `TextParser.cs` - Command parsing and matching
- `Player.cs` - Character rendering and movement
- `Room.cs` - Abstract base for all rooms
- `RetroGraphics.cs` - Retro rendering utilities
- `*Room.cs` - Individual room implementations

### Retro Graphics System
- EGA 16-color palette (Black, Blue, Green, Cyan, Red, Magenta, Brown, LightGray, etc.)
- Dithering patterns for depth
- No anti-aliasing or smooth gradients
- Pixel-perfect text rendering
- Depth-based object sorting

## Game Tips

1. **Explore everything** - Examining objects gives you points!
2. **Check the kitchen first** - The car keys are essential for winning
3. **Read the note in the garage** - It gives you a hint
4. **Type commands** - The text parser accepts natural language
5. **Try different verbs** - LOOK, TAKE, USE, OPEN, EXAMINE all work
6. **Maximum score is 100 points** - Can you get them all?

## Sierra Online Inspiration

This game is inspired by classic Sierra Online adventures:
- **King's Quest** series (1-7)
- **Space Quest** series
- **Police Quest** series
- **Leisure Suit Larry** series

Features authentic AGI/SCI engine aesthetics:
- EGA 16-color graphics
- Text parser interface
- Score system
- Room-based exploration
- Inventory management
- Win/lose conditions

## Future Enhancements

- More rooms (attic, basement, backyard)
- More puzzles (locked doors, combinations, item combinations)
- NPCs and dialogue trees
- Sound effects (beeps and boops)
- Music (chiptune MIDI)
- Death scenes (classic Sierra style)
- Save/Load system
- Multiple endings

## Credits

Built with C# and Windows Forms
Inspired by Sierra Online's AGI/SCI engines
Created as a retro gaming tribute

---

**Enjoy your retro adventure!** 🎮👾

Type `help` in-game for command hints!
