(function (BookWriter) {
  "use strict";

  var catalogue = [
    {
      id: "context",
      label: "Context",
      text: "If you would like, what was happening in your life around this memory?"
    },
    {
      id: "people",
      label: "People",
      text: "Who was present or important, and what would you like a reader to understand about them?"
    },
    {
      id: "setting",
      label: "Setting",
      text: "If you would like, what details about the place or time might help you return to the scene?"
    },
    {
      id: "senses",
      label: "Senses",
      text: "If you want, are there sounds, smells, textures, tastes, colors, or other sensory details to include?"
    },
    {
      id: "emotions",
      label: "Feelings",
      text: "If you would like, what feelings do you remember then or notice now?"
    },
    {
      id: "significance",
      label: "Significance",
      text: "What makes this memory meaningful to you, if you want to reflect on that?"
    },
    {
      id: "beforeAfter",
      label: "Before and after",
      text: "If it helps, what happened before this moment, and what happened afterward?"
    }
  ].map(function (prompt) {
    return Object.freeze(prompt);
  });

  BookWriter.Prompts = Object.freeze({
    catalogue: Object.freeze(catalogue)
  });
}(window.BookWriter));
