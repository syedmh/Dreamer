(function (BookWriter) {
  "use strict";

  var PROMPT_IDS = [
    "context",
    "people",
    "setting",
    "senses",
    "emotions",
    "significance",
    "beforeAfter"
  ];

  function success(value) {
    return { ok: true, value: value };
  }

  function failure(code, message, details) {
    var error = { code: code, message: message };
    if (details !== undefined) {
      error.details = details;
    }
    return { ok: false, error: error };
  }

  function clone(value) {
    return JSON.parse(JSON.stringify(value));
  }

  function isObject(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value);
  }

  function isStringArray(value) {
    return Array.isArray(value) && value.every(function (item) {
      return typeof item === "string";
    });
  }

  function emptyPromptResponses() {
    var responses = {};
    PROMPT_IDS.forEach(function (id) {
      responses[id] = "";
    });
    return responses;
  }

  function createEmptyCollection(now, ids) {
    if (typeof now !== "string" || !ids || typeof ids.collectionId !== "function") {
      throw new TypeError("createEmptyCollection requires an ISO timestamp and ID factory.");
    }

    return {
      schemaVersion: 1,
      collectionId: ids.collectionId(),
      revision: 0,
      createdAt: now,
      updatedAt: now,
      book: {
        title: "",
        subtitle: "",
        authorName: "",
        dedication: "",
        preface: ""
      },
      chapters: [],
      unassignedMemoryIds: [],
      memories: {},
      settings: {
        backupReminderDismissedAt: null
      }
    };
  }

  function findChapter(collection, chapterId) {
    var index = -1;
    collection.chapters.some(function (chapter, candidateIndex) {
      if (chapter.id === chapterId) {
        index = candidateIndex;
        return true;
      }
      return false;
    });
    return index;
  }

  function findMemoryList(collection, memoryId) {
    var found = null;
    collection.chapters.some(function (chapter) {
      var index = chapter.memoryIds.indexOf(memoryId);
      if (index !== -1) {
        found = { list: chapter.memoryIds, index: index, chapterId: chapter.id };
        return true;
      }
      return false;
    });

    if (found) {
      return found;
    }

    var unassignedIndex = collection.unassignedMemoryIds.indexOf(memoryId);
    if (unassignedIndex !== -1) {
      return {
        list: collection.unassignedMemoryIds,
        index: unassignedIndex,
        chapterId: null
      };
    }
    return null;
  }

  function removeMemoryReference(collection, memoryId) {
    var location = findMemoryList(collection, memoryId);
    if (!location) {
      return false;
    }
    location.list.splice(location.index, 1);
    return true;
  }

  function applyBookFields(book, fields) {
    var allowed = ["title", "subtitle", "authorName", "dedication", "preface"];
    if (!isObject(fields)) {
      return false;
    }
    return Object.keys(fields).every(function (key) {
      if (allowed.indexOf(key) === -1 || typeof fields[key] !== "string") {
        return false;
      }
      book[key] = fields[key];
      return true;
    });
  }

  function applyMemoryFields(memory, fields) {
    var stringFields = [
      "title",
      "memoryText",
      "dateText",
      "sensoryDetails"
    ];
    var arrayFields = ["people", "places", "themes"];

    if (!isObject(fields)) {
      return false;
    }

    return Object.keys(fields).every(function (key) {
      if (stringFields.indexOf(key) !== -1 && typeof fields[key] === "string") {
        memory[key] = fields[key];
        return true;
      }
      if (arrayFields.indexOf(key) !== -1 && isStringArray(fields[key])) {
        memory[key] = fields[key].slice();
        return true;
      }
      return false;
    });
  }

  function createMemory(id, fields, now) {
    var memory = {
      id: id,
      title: "",
      memoryText: "",
      dateText: "",
      people: [],
      places: [],
      themes: [],
      sensoryDetails: "",
      promptResponses: emptyPromptResponses(),
      narrativeText: "",
      createdAt: now,
      updatedAt: now
    };
    return applyMemoryFields(memory, fields || {}) ? memory : null;
  }

  function apply(collection, command, now) {
    if (!isObject(collection) || !isObject(command) || typeof command.type !== "string" ||
        typeof now !== "string") {
      return failure("INVALID_SCHEMA", "The requested change is not valid.");
    }

    var next = clone(collection);
    var memory;
    var chapterIndex;
    var chapter;
    var direction;
    var swapIndex;
    var targetList;
    var targetIndex;

    switch (command.type) {
    case "UPDATE_BOOK":
      if (!applyBookFields(next.book, command.fields)) {
        return failure("INVALID_SCHEMA", "Book fields are invalid.");
      }
      break;

    case "CREATE_MEMORY":
      if (typeof command.id !== "string" || !command.id ||
          Object.prototype.hasOwnProperty.call(next.memories, command.id)) {
        return failure("DUPLICATE_ID", "The memory ID is missing or already exists.");
      }
      memory = createMemory(command.id, command.fields, now);
      if (!memory) {
        return failure("INVALID_SCHEMA", "Memory fields are invalid.");
      }
      next.memories[command.id] = memory;
      next.unassignedMemoryIds.push(command.id);
      break;

    case "UPDATE_MEMORY":
      memory = next.memories[command.memoryId];
      if (!memory) {
        return failure("INVALID_REFERENCE", "The memory could not be found.");
      }
      if (!applyMemoryFields(memory, command.fields)) {
        return failure("INVALID_SCHEMA", "Memory fields are invalid.");
      }
      memory.updatedAt = now;
      break;

    case "DELETE_MEMORY":
      if (command.confirmationToken !== "DELETE_MEMORY") {
        return failure("INVALID_SCHEMA", "Memory deletion was not confirmed.");
      }
      if (!next.memories[command.memoryId]) {
        return failure("INVALID_REFERENCE", "The memory could not be found.");
      }
      if (!removeMemoryReference(next, command.memoryId)) {
        return failure("INVALID_REFERENCE", "The memory ordering reference is missing.");
      }
      delete next.memories[command.memoryId];
      break;

    case "CREATE_CHAPTER":
      if (typeof command.id !== "string" || !command.id ||
          findChapter(next, command.id) !== -1) {
        return failure("DUPLICATE_ID", "The chapter ID is missing or already exists.");
      }
      if (typeof command.title !== "string" || !command.title.trim()) {
        return failure("INVALID_SCHEMA", "A chapter title is required.");
      }
      next.chapters.push({
        id: command.id,
        title: command.title,
        createdAt: now,
        updatedAt: now,
        memoryIds: []
      });
      break;

    case "RENAME_CHAPTER":
      chapterIndex = findChapter(next, command.chapterId);
      if (chapterIndex === -1) {
        return failure("INVALID_REFERENCE", "The chapter could not be found.");
      }
      if (typeof command.title !== "string" || !command.title.trim()) {
        return failure("INVALID_SCHEMA", "A chapter title is required.");
      }
      next.chapters[chapterIndex].title = command.title;
      next.chapters[chapterIndex].updatedAt = now;
      break;

    case "MOVE_CHAPTER":
      chapterIndex = findChapter(next, command.chapterId);
      direction = command.direction;
      if (chapterIndex === -1) {
        return failure("INVALID_REFERENCE", "The chapter could not be found.");
      }
      if (direction !== "up" && direction !== "down") {
        return failure("INVALID_SCHEMA", "Chapter direction must be up or down.");
      }
      swapIndex = direction === "up" ? chapterIndex - 1 : chapterIndex + 1;
      if (swapIndex >= 0 && swapIndex < next.chapters.length) {
        chapter = next.chapters[chapterIndex];
        next.chapters[chapterIndex] = next.chapters[swapIndex];
        next.chapters[swapIndex] = chapter;
      }
      break;

    case "DELETE_CHAPTER":
      if (command.confirmationToken !== "DELETE_CHAPTER") {
        return failure("INVALID_SCHEMA", "Chapter deletion was not confirmed.");
      }
      chapterIndex = findChapter(next, command.chapterId);
      if (chapterIndex === -1) {
        return failure("INVALID_REFERENCE", "The chapter could not be found.");
      }
      chapter = next.chapters[chapterIndex];
      next.unassignedMemoryIds = next.unassignedMemoryIds.concat(chapter.memoryIds);
      next.chapters.splice(chapterIndex, 1);
      break;

    case "MOVE_MEMORY":
      if (!next.memories[command.memoryId]) {
        return failure("INVALID_REFERENCE", "The memory could not be found.");
      }
      if (!Number.isInteger(command.targetIndex) || command.targetIndex < 0) {
        return failure("INVALID_SCHEMA", "The memory position is invalid.");
      }
      if (command.targetChapterId === null) {
        targetList = next.unassignedMemoryIds;
      } else {
        chapterIndex = findChapter(next, command.targetChapterId);
        if (chapterIndex === -1) {
          return failure("INVALID_REFERENCE", "The target chapter could not be found.");
        }
        targetList = next.chapters[chapterIndex].memoryIds;
      }
      if (!removeMemoryReference(next, command.memoryId)) {
        return failure("INVALID_REFERENCE", "The memory ordering reference is missing.");
      }
      targetIndex = Math.min(command.targetIndex, targetList.length);
      targetList.splice(targetIndex, 0, command.memoryId);
      break;

    case "UPDATE_PROMPT_RESPONSE":
      memory = next.memories[command.memoryId];
      if (!memory) {
        return failure("INVALID_REFERENCE", "The memory could not be found.");
      }
      if (PROMPT_IDS.indexOf(command.promptId) === -1 || typeof command.text !== "string") {
        return failure("INVALID_SCHEMA", "The prompt response is invalid.");
      }
      memory.promptResponses[command.promptId] = command.text;
      memory.updatedAt = now;
      break;

    case "UPDATE_NARRATIVE":
      memory = next.memories[command.memoryId];
      if (!memory) {
        return failure("INVALID_REFERENCE", "The memory could not be found.");
      }
      if (typeof command.text !== "string") {
        return failure("INVALID_SCHEMA", "Narrative text must be text.");
      }
      memory.narrativeText = command.text;
      memory.updatedAt = now;
      break;

    case "ERASE_COLLECTION":
      if (command.confirmationToken !== "ERASE_ALL" ||
          typeof command.newCollectionId !== "string" || !command.newCollectionId) {
        return failure("INVALID_SCHEMA", "Collection erasure was not confirmed.");
      }
      next = createEmptyCollection(now, {
        collectionId: function () {
          return command.newCollectionId;
        }
      });
      break;

    default:
      return failure("INVALID_SCHEMA", "The requested change is not supported.");
    }

    next.updatedAt = now;
    return success(next);
  }

  function orderedMemoryIds(collection) {
    var ids = [];
    collection.chapters.forEach(function (chapter) {
      ids = ids.concat(chapter.memoryIds);
    });
    return ids.concat(collection.unassignedMemoryIds);
  }

  function search(collection, query, filters) {
    var normalizedQuery = String(query || "").trim().toLocaleLowerCase();
    var actualFilters = filters || {};
    var hasChapterFilter = Object.prototype.hasOwnProperty.call(actualFilters, "chapterId");
    var themeFilter = String(actualFilters.theme || "").trim().toLocaleLowerCase();
    var candidateIds;

    if (hasChapterFilter && actualFilters.chapterId === null) {
      candidateIds = collection.unassignedMemoryIds.slice();
    } else if (hasChapterFilter) {
      var chapterIndex = findChapter(collection, actualFilters.chapterId);
      candidateIds = chapterIndex === -1 ? [] : collection.chapters[chapterIndex].memoryIds.slice();
    } else {
      candidateIds = orderedMemoryIds(collection);
    }

    return candidateIds.filter(function (memoryId) {
      var memory = collection.memories[memoryId];
      if (!memory) {
        return false;
      }
      if (themeFilter && !memory.themes.some(function (theme) {
        return theme.toLocaleLowerCase() === themeFilter;
      })) {
        return false;
      }
      if (!normalizedQuery) {
        return true;
      }
      return [
        memory.title,
        memory.memoryText,
        memory.people.join(" "),
        memory.places.join(" "),
        memory.themes.join(" ")
      ].some(function (value) {
        return value.toLocaleLowerCase().indexOf(normalizedQuery) !== -1;
      });
    });
  }

  function memoryParagraph(memory) {
    var heading = memory.title || "Untitled memory";
    if (memory.dateText) {
      heading += " (" + memory.dateText + ")";
    }
    return heading + "\n\n" + (memory.narrativeText || memory.memoryText || "");
  }

  function exportMemory(memory) {
    return {
      id: memory.id,
      title: memory.title,
      dateText: memory.dateText,
      memoryText: memory.memoryText,
      narrativeText: memory.narrativeText,
      people: memory.people.slice(),
      places: memory.places.slice(),
      themes: memory.themes.slice(),
      sensoryDetails: memory.sensoryDetails,
      promptResponses: clone(memory.promptResponses),
      createdAt: memory.createdAt,
      updatedAt: memory.updatedAt
    };
  }

  function chapterSection(collection, sectionId, title, memoryIds, includeStructuredFields) {
    var section = {
      type: "chapter",
      id: sectionId,
      title: title,
      paragraphs: memoryIds.map(function (memoryId) {
        return memoryParagraph(collection.memories[memoryId]);
      })
    };

    if (includeStructuredFields) {
      section.memories = memoryIds.map(function (memoryId) {
        return exportMemory(collection.memories[memoryId]);
      });
    }

    return section;
  }

  function assembleBook(collection, options) {
    var includeStructuredFields = !!(options && options.includeStructuredFields);
    var bookTitle = collection.book.title || "My Life Story";
    var frontParagraphs = [];
    if (collection.book.subtitle) {
      frontParagraphs.push(collection.book.subtitle);
    }
    if (collection.book.authorName) {
      frontParagraphs.push("By " + collection.book.authorName);
    }
    if (collection.book.dedication) {
      frontParagraphs.push("Dedication\n\n" + collection.book.dedication);
    }
    if (collection.book.preface) {
      frontParagraphs.push("Preface\n\n" + collection.book.preface);
    }

    var sections = [{
      type: "frontMatter",
      title: bookTitle,
      paragraphs: frontParagraphs
    }];

    collection.chapters.forEach(function (chapter) {
      sections.push(chapterSection(collection, chapter.id, chapter.title,
        chapter.memoryIds.slice(), includeStructuredFields));
    });

    if (collection.unassignedMemoryIds.length) {
      sections.push(chapterSection(collection, "unassigned", "Unassigned Memories",
        collection.unassignedMemoryIds.slice(), includeStructuredFields));
    }

    return sections;
  }

  BookWriter.Domain = Object.freeze({
    PROMPT_IDS: PROMPT_IDS.slice(),
    createEmptyCollection: createEmptyCollection,
    apply: apply,
    search: search,
    assembleBook: assembleBook
  });
}(window.BookWriter));
