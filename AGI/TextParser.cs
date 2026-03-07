using System;
using System.Collections.Generic;
using System.Linq;

namespace AGIGame
{
    public class TextParser
    {
        private static readonly string[] LookVerbs = { "look", "examine", "inspect", "l", "x" };
        private static readonly string[] TakeVerbs = { "take", "get", "pick", "grab", "pickup" };
        private static readonly string[] UseVerbs = { "use", "open", "close" };
        private static readonly string[] TalkVerbs = { "talk", "speak", "ask" };
        private static readonly string[] GoVerbs = { "go", "walk", "move", "enter" };

        public static ParsedCommand Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new ParsedCommand { IsValid = false, Message = "Please enter a command." };

            input = input.ToLower().Trim();

            // Remove common filler words
            input = input.Replace(" at ", " ")
                        .Replace(" the ", " ")
                        .Replace(" a ", " ")
                        .Replace(" to ", " ")
                        .Replace(" on ", " ")
                        .Replace("  ", " ");

            string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                return new ParsedCommand { IsValid = false, Message = "Please enter a command." };

            string verb = parts[0];
            string target = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";

            // Determine command type
            CommandType type = DetermineCommandType(verb);

            if (type == CommandType.Unknown)
            {
                return new ParsedCommand
                {
                    IsValid = false,
                    Message = $"I don't understand '{verb}'. Try LOOK, TAKE, USE, or GO."
                };
            }

            return new ParsedCommand
            {
                IsValid = true,
                Type = type,
                Verb = verb,
                Target = target,
                OriginalInput = input
            };
        }

        private static CommandType DetermineCommandType(string verb)
        {
            if (LookVerbs.Contains(verb)) return CommandType.Look;
            if (TakeVerbs.Contains(verb)) return CommandType.Take;
            if (UseVerbs.Contains(verb)) return CommandType.Use;
            if (TalkVerbs.Contains(verb)) return CommandType.Talk;
            if (GoVerbs.Contains(verb)) return CommandType.Go;

            return CommandType.Unknown;
        }

        public static bool MatchesObject(string target, string objectName)
        {
            if (string.IsNullOrWhiteSpace(target))
                return false;

            string normalizedTarget = target.ToLower().Replace(" ", "");
            string normalizedObject = objectName.ToLower().Replace(" ", "");

            // Exact match
            if (normalizedTarget == normalizedObject)
                return true;

            // Partial match (target contains object name or vice versa)
            if (normalizedObject.Contains(normalizedTarget) ||
                normalizedTarget.Contains(normalizedObject))
                return true;

            // Check individual words
            string[] targetWords = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] objectWords = objectName.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (string tw in targetWords)
            {
                foreach (string ow in objectWords)
                {
                    if (tw == ow && tw.Length > 2) // Match words longer than 2 chars
                        return true;
                }
            }

            return false;
        }
    }

    public class ParsedCommand
    {
        public bool IsValid { get; set; }
        public CommandType Type { get; set; }
        public string Verb { get; set; } = "";
        public string Target { get; set; } = "";
        public string OriginalInput { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public enum CommandType
    {
        Unknown,
        Look,
        Take,
        Use,
        Talk,
        Go
    }
}
