const CENTER = 0.5;
const STAGE_Y = 0.9;
const TURN_RIGHT_PROMPT = "Start middle of screen front facing then turn right";

export function compilePrompt(prompt, characters) {
  if (typeof prompt !== "string" || prompt.length === 0) {
    throw new Error("Prompt compilation error: prompt must be a non-empty string");
  }
  if (!characters || typeof characters !== "object") {
    throw new Error("Prompt compilation error: characters must be provided");
  }

  if (prompt === TURN_RIGHT_PROMPT) {
    if (!characters.boy) {
      throw new Error('Prompt compilation error: character "boy" is required');
    }
    return [
      { type: "spawn", actor: "boy", x: CENTER, y: STAGE_Y, facing: "front", animation: "front" },
      { type: "turn", actor: "boy", facing: "right", durationMs: 1600, fps: 6 }
    ];
  }

  throw new Error(`Prompt compilation error: unsupported prompt "${prompt}"`);
}
