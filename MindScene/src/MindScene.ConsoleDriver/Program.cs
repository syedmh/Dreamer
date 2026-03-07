using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MindScene.AI;
using MindScene.Core.Models;
using MindScene.Core.Pipeline;
using MindScene.DialogueEngine;
using MindScene.Export;
using MindScene.NLP;
using MindScene.RenderEngine;
using System.Text.Json;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.SetBasePath(Directory.GetCurrentDirectory())
           .AddJsonFile("appsettings.json", optional: true)
           .AddEnvironmentVariables("MINDSCENE_")
           .AddUserSecrets<Program>(optional: true);
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole(c => c.FormatterName = "simple");
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;
        services.AddMindSceneAi(config);
        services.AddMindSceneNlp();
        services.AddMindSceneDialogueEngine(config);
        services.AddMindSceneRenderEngine();
        services.AddMindSceneExport();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<GenerateSceneCommand>());
    })
    .Build();

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

string inputPath = args[0];
string outputPath = args.Length >= 2 ? args[1] : Path.ChangeExtension(inputPath, ".mp4");

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Error: Input file not found: {inputPath}");
    return 1;
}

SceneInput sceneInput;
try
{
    var json = await File.ReadAllTextAsync(inputPath);
    sceneInput = JsonSerializer.Deserialize<SceneInput>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Failed to parse scene JSON");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error reading scene file: {ex.Message}");
    return 1;
}

Console.WriteLine($"""
  =======================================
  MindScene Engine v0.1  (Text to MP4)
  =======================================
  Input:  {inputPath}
  Output: {outputPath}
  Style:  {sceneInput.Style}
  Actors: {sceneInput.Actors.Count}
  Lines:  {sceneInput.Dialogue.Count}
  """);

var mediator = host.Services.GetRequiredService<IMediator>();
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var result = await mediator.Send(new GenerateSceneCommand(sceneInput, outputPath), cts.Token);

if (result.IsSuccess)
{
    Console.WriteLine($"\nDone! Output: {result.Value}");
    return 0;
}
else
{
    Console.Error.WriteLine($"\nFailed: {result.Error}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        MindScene -- Text-to-Animation Engine

        Usage:
          MindScene.ConsoleDriver <scene.json> [output.mp4]

        Environment variables:
          MINDSCENE_AI__APIKEY     -- Anthropic API key (required)
          MINDSCENE_TTS__PROVIDER  -- Stub (default) | ElevenLabs
          MINDSCENE_TTS__APIKEY    -- ElevenLabs API key
        """);
}
