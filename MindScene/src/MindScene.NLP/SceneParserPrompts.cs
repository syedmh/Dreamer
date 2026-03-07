namespace MindScene.NLP;

internal static class SceneParserPrompts
{
    public const string SceneDecompositionSystem =
        "You are an expert scene analysis AI for an animation engine. " +
        "Your job is to analyze scene descriptions and extract structured information. " +
        "Always respond with valid JSON matching the schema provided. No markdown, no explanations — pure JSON only.";

    public static string BuildSceneDecompositionPrompt(string rawDescription, string mood, string style) =>
        "Analyze this scene description and extract structured information.\n\n" +
        $"Scene: \"{rawDescription}\"\nMood: {mood}\nStyle: {style}\n\n" +
        "Return JSON with this exact schema:\n" +
        "{\n" +
        "  \"location\": \"rooftop\",\n" +
        "  \"timeOfDay\": \"night\",\n" +
        "  \"weather\": \"clear\",\n" +
        "  \"lighting\": \"dim\",\n" +
        "  \"atmosphere\": \"bittersweet\",\n" +
        "  \"environmentTemplate\": \"rooftop\",\n" +
        "  \"props\": [\"railing\"],\n" +
        "  \"ambientSound\": \"city noise\",\n" +
        "  \"actorCount\": 2\n" +
        "}";

    public static string BuildSpatialLayoutPrompt(string rawDescription, List<string> actorNames) =>
        "For this scene, determine the spatial layout for each actor.\n\n" +
        $"Scene: \"{rawDescription}\"\nActors: {string.Join(", ", actorNames)}\n\n" +
        "Frame: X=0 left, X=1 right, Y=0 top, Y=1 bottom. Standing characters: Y≈0.6-0.8.\n\n" +
        "Return a JSON array, one entry per actor:\n" +
        "[\n" +
        "  {\n" +
        "    \"name\": \"ActorName\",\n" +
        "    \"x\": 0.35,\n" +
        "    \"y\": 0.70,\n" +
        "    \"zLayer\": 0.5,\n" +
        "    \"facingDirection\": \"front\",\n" +
        "    \"pose\": \"standing\",\n" +
        "    \"scale\": 1.0\n" +
        "  }\n" +
        "]";

    public static string BuildAnimationDirectivesPrompt(string rawDescription, List<string> dialogueActions) =>
        "Generate animation directives for this scene.\n\n" +
        $"Scene: \"{rawDescription}\"\nActions: {string.Join("; ", dialogueActions)}\n\n" +
        "Valid actions: Idle, Walk, Sit, Stand, TurnLeft, TurnRight, Gesture, Nod, ShakeHead, LookAt, LookAway, React, EmotionTransition\n\n" +
        "Return a JSON array sorted by startSeconds:\n" +
        "[\n" +
        "  {\n" +
        "    \"actorName\": \"Name\",\n" +
        "    \"action\": \"Idle\",\n" +
        "    \"startSeconds\": 0.0,\n" +
        "    \"durationSeconds\": 2.0,\n" +
        "    \"targetEmotion\": { \"joy\": 0.0, \"sadness\": 0.8, \"anger\": 0.0, \"fear\": 0.0, \"surprise\": 0.0, \"disgust\": 0.0 }\n" +
        "  }\n" +
        "]";
}
