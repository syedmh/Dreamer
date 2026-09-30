(function (BookWriter) {
  "use strict";

  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function joinedList(values) {
    return values.length ? values.join(", ") : "";
  }

  function promptEntries(memory) {
    return BookWriter.Prompts.catalogue.map(function (prompt) {
      return {
        label: prompt.label,
        text: memory.promptResponses[prompt.id]
      };
    }).filter(function (entry) {
      return entry.text && entry.text.trim();
    });
  }

  function appendHtmlMetadata(output, label, value) {
    if (!value) {
      return;
    }
    output.push("<p class=\"memory-meta\"><strong>" + escapeHtml(label) +
      ":</strong> " + escapeHtml(value) + "</p>");
  }

  function appendHtmlBlock(output, heading, text) {
    if (!text) {
      return;
    }
    output.push("<section class=\"memory-block\">");
    output.push("<h4>" + escapeHtml(heading) + "</h4>");
    output.push("<p>" + escapeHtml(text) + "</p>");
    output.push("</section>");
  }

  function appendHtmlPrompts(output, memory) {
    var prompts = promptEntries(memory);
    if (!prompts.length) {
      return;
    }
    output.push("<section class=\"memory-block\">");
    output.push("<h4>Prompt responses</h4>");
    prompts.forEach(function (prompt) {
      output.push("<h5>" + escapeHtml(prompt.label) + "</h5>");
      output.push("<p>" + escapeHtml(prompt.text) + "</p>");
    });
    output.push("</section>");
  }

  function appendHtmlMemory(output, memory) {
    output.push("<article class=\"memory-entry\">");
    output.push("<h3>" + escapeHtml(memory.title || "Untitled memory") + "</h3>");
    appendHtmlMetadata(output, "Approximate date", memory.dateText);
    appendHtmlMetadata(output, "People", joinedList(memory.people));
    appendHtmlMetadata(output, "Places", joinedList(memory.places));
    appendHtmlMetadata(output, "Themes", joinedList(memory.themes));
    appendHtmlMetadata(output, "Sensory details", memory.sensoryDetails);
    appendHtmlMetadata(output, "Created", memory.createdAt);
    appendHtmlMetadata(output, "Last updated", memory.updatedAt);
    appendHtmlBlock(output, "Source memory", memory.memoryText);
    appendHtmlBlock(output, "Narrative", memory.narrativeText);
    appendHtmlPrompts(output, memory);
    output.push("</article>");
  }

  function appendTextMetadata(lines, label, value) {
    if (!value) {
      return;
    }
    lines.push(label + ": " + value);
  }

  function appendTextBlock(lines, heading, text) {
    if (!text) {
      return;
    }
    lines.push("");
    lines.push(heading);
    lines.push("");
    lines.push(text);
  }

  function appendTextPrompts(lines, memory) {
    var prompts = promptEntries(memory);
    if (!prompts.length) {
      return;
    }
    lines.push("");
    lines.push("Prompt responses");
    prompts.forEach(function (prompt) {
      lines.push("");
      lines.push(prompt.label);
      lines.push(prompt.text);
    });
  }

  function appendTextMemory(lines, memory) {
    lines.push("");
    lines.push("Memory: " + (memory.title || "Untitled memory"));
    appendTextMetadata(lines, "Approximate date", memory.dateText);
    appendTextMetadata(lines, "People", joinedList(memory.people));
    appendTextMetadata(lines, "Places", joinedList(memory.places));
    appendTextMetadata(lines, "Themes", joinedList(memory.themes));
    appendTextMetadata(lines, "Sensory details", memory.sensoryDetails);
    appendTextMetadata(lines, "Created", memory.createdAt);
    appendTextMetadata(lines, "Last updated", memory.updatedAt);
    appendTextBlock(lines, "Source memory", memory.memoryText);
    appendTextBlock(lines, "Narrative", memory.narrativeText);
    appendTextPrompts(lines, memory);
  }

  function toHtml(collection) {
    var sections = BookWriter.Domain.assembleBook(collection, {
      includeStructuredFields: true
    });
    var title = sections.length ? sections[0].title : "My Life Story";
    var output = [
      "<!doctype html>",
      "<html lang=\"en\">",
      "<head>",
      "<meta charset=\"utf-8\">",
      "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
      "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">",
      "<title>" + escapeHtml(title) + "</title>",
      "<style>",
      "body{max-width:48rem;margin:2rem auto;padding:0 1rem;color:#202124;background:#fff;font:18px/1.6 Georgia,serif}",
      "h1,h2,h3,h4,h5{line-height:1.2;color:#172554}section{margin:3rem 0}p{white-space:pre-wrap}",
      ".memory-entry{margin:2rem 0;padding:1rem 1.25rem;border:1px solid #d1d5db;border-radius:.5rem;background:#f9fafb}",
      ".memory-entry h3{margin-bottom:.75rem}.memory-block{margin:1.5rem 0}.memory-block h4,.memory-block h5{margin-bottom:.35rem}.memory-meta{margin:.35rem 0}",
      "@media print{body{max-width:none;margin:0}section{break-before:page}.front-matter{break-before:auto}.memory-entry{break-inside:avoid}}",
      "</style>",
      "</head>",
      "<body>"
    ];

    sections.forEach(function (section, index) {
      output.push("<section class=\"" +
        (section.type === "frontMatter" ? "front-matter" : "chapter") + "\">");
      if (section.type === "frontMatter") {
        output.push(index === 0 ?
          "<h1>" + escapeHtml(section.title) + "</h1>" :
          "<h2>" + escapeHtml(section.title) + "</h2>");
        section.paragraphs.forEach(function (paragraph) {
          output.push("<p>" + escapeHtml(paragraph) + "</p>");
        });
      } else {
        output.push("<h2>" + escapeHtml(section.title) + "</h2>");
        if (!section.memories || !section.memories.length) {
          output.push("<p>This chapter has no memories yet.</p>");
        } else {
          section.memories.forEach(function (memory) {
            appendHtmlMemory(output, memory);
          });
        }
      }
      output.push("</section>");
    });

    output.push("</body>", "</html>");
    return output.join("\n");
  }

  function toText(collection) {
    var sections = BookWriter.Domain.assembleBook(collection, {
      includeStructuredFields: true
    });
    var lines = [];
    sections.forEach(function (section, index) {
      if (index > 0) {
        lines.push("");
        lines.push("");
      }
      lines.push(section.title);
      lines.push(index === 0 ?
        new Array(section.title.length + 1).join("=") :
        new Array(section.title.length + 1).join("-"));
      if (section.type === "frontMatter") {
        section.paragraphs.forEach(function (paragraph) {
          lines.push("");
          lines.push(paragraph);
        });
        return;
      }
      if (!section.memories || !section.memories.length) {
        lines.push("");
        lines.push("This chapter has no memories yet.");
        return;
      }
      section.memories.forEach(function (memory) {
        appendTextMemory(lines, memory);
      });
    });
    return lines.join("\n") + "\n";
  }

  BookWriter.Export = Object.freeze({
    toHtml: toHtml,
    toText: toText
  });
}(window.BookWriter));
