(function (BookWriter) {
  "use strict";

  var MAX_BACKUP_BYTES = 10 * 1024 * 1024;
  var RESTORE_LIMIT_GUIDANCE =
    " The current memoir was not changed. Split a very large collection into smaller collections before restoring.";
  var UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
  var PROMPT_IDS = [
    "context",
    "people",
    "setting",
    "senses",
    "emotions",
    "significance",
    "beforeAfter"
  ];
  var LIMITS = Object.freeze({
    chapters: 50,
    memories: 500,
    referenceListCount: 500,
    listCount: 100,
    promptCount: PROMPT_IDS.length,
    bookTitleLength: 300,
    bookSubtitleLength: 300,
    authorNameLength: 300,
    dedicationLength: 20000,
    prefaceLength: 50000,
    chapterTitleLength: 200,
    memoryTitleLength: 300,
    dateTextLength: 200,
    listItemLength: 200,
    sensoryDetailsLength: 5000,
    memoryTextLength: 100000,
    narrativeTextLength: 100000,
    promptResponseLength: 50000,
    aggregateCharacters: 600000
  });

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

  function hasExactKeys(value, expected) {
    if (!isObject(value)) {
      return false;
    }
    var actual = Object.keys(value).sort();
    var wanted = expected.slice().sort();
    return actual.length === wanted.length && actual.every(function (key, index) {
      return key === wanted[index];
    });
  }

  function isIsoTimestamp(value) {
    if (typeof value !== "string" || !value) {
      return false;
    }
    var parsed = new Date(value);
    return !Number.isNaN(parsed.getTime()) && parsed.toISOString() === value;
  }

  function isUuid(value) {
    return typeof value === "string" && UUID_PATTERN.test(value);
  }

  function isTextArray(value) {
    return Array.isArray(value) && value.every(function (item) {
      return typeof item === "string";
    });
  }

  function invalidSchema(path, message, extraDetails) {
    var details = { path: path };
    if (extraDetails) {
      Object.keys(extraDetails).forEach(function (key) {
        details[key] = extraDetails[key];
      });
    }
    return failure("INVALID_SCHEMA", message || "The collection schema is invalid.", details);
  }

  function validateCountLimit(path, actual, maximum, message, enforceResourceLimits) {
    if (!enforceResourceLimits) {
      return success(actual);
    }
    if (actual > maximum) {
      return invalidSchema(path, message + RESTORE_LIMIT_GUIDANCE, {
        actual: actual,
        maximum: maximum
      });
    }
    return success(actual);
  }

  function validateTextLimit(value, path, maximumLength, aggregate, message,
      enforceResourceLimits) {
    if (typeof value !== "string") {
      return invalidSchema(path, message || "A text value is invalid.");
    }
    if (!enforceResourceLimits) {
      return success(value);
    }
    if (value.length > maximumLength) {
      return invalidSchema(path,
        "A text field exceeds the safe restore length." + RESTORE_LIMIT_GUIDANCE, {
        maximumLength: maximumLength,
        actualLength: value.length
      });
    }
    aggregate.total += value.length;
    if (aggregate.total > LIMITS.aggregateCharacters) {
      return invalidSchema(path,
        "The backup exceeds the safe restore aggregate text length." +
        RESTORE_LIMIT_GUIDANCE, {
          maximumLength: LIMITS.aggregateCharacters,
          actualLength: aggregate.total
        });
    }
    return success(value);
  }

  function validateTextList(value, path, aggregate, enforceResourceLimits) {
    if (!isTextArray(value)) {
      return invalidSchema(path, "People, places, and themes must be text lists.");
    }
    var countLimit = validateCountLimit(path, value.length, LIMITS.listCount,
      "A text list exceeds the safe restore item count.", enforceResourceLimits);
    if (!countLimit.ok) {
      return countLimit;
    }
    for (var index = 0; index < value.length; index += 1) {
      var itemResult = validateTextLimit(value[index], path + "[" + index + "]",
        LIMITS.listItemLength, aggregate, "A text list item is invalid.",
        enforceResourceLimits);
      if (!itemResult.ok) {
        return itemResult;
      }
    }
    return success(value);
  }

  function validateUuidList(value, path, enforceResourceLimits) {
    if (!Array.isArray(value) || !value.every(isUuid)) {
      return invalidSchema(path, "Ordered memory references must be UUIDs.");
    }
    return validateCountLimit(path, value.length, LIMITS.referenceListCount,
      "An ordered memory list exceeds the safe restore item count.",
      enforceResourceLimits);
  }

  function validateBook(book, aggregate, enforceResourceLimits) {
    if (!hasExactKeys(book, ["title", "subtitle", "authorName", "dedication", "preface"])) {
      return invalidSchema("book", "Book metadata fields are missing or unexpected.");
    }
    var result = validateTextLimit(book.title, "book.title",
      LIMITS.bookTitleLength, aggregate, "Book metadata must contain text values.",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextLimit(book.subtitle, "book.subtitle",
      LIMITS.bookSubtitleLength, aggregate, "Book metadata must contain text values.",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextLimit(book.authorName, "book.authorName",
      LIMITS.authorNameLength, aggregate, "Book metadata must contain text values.",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextLimit(book.dedication, "book.dedication",
      LIMITS.dedicationLength, aggregate, "Book metadata must contain text values.",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    return validateTextLimit(book.preface, "book.preface",
      LIMITS.prefaceLength, aggregate, "Book metadata must contain text values.",
      enforceResourceLimits);
  }

  function validatePromptResponses(responses, path, aggregate, enforceResourceLimits) {
    if (!hasExactKeys(responses, PROMPT_IDS)) {
      return invalidSchema(path, "Prompt response fields are missing or unexpected.");
    }
    var countLimit = validateCountLimit(path, Object.keys(responses).length, LIMITS.promptCount,
      "Prompt responses exceed the safe restore prompt count.", enforceResourceLimits);
    if (!countLimit.ok) {
      return countLimit;
    }
    for (var index = 0; index < PROMPT_IDS.length; index += 1) {
      var promptId = PROMPT_IDS[index];
      var response = validateTextLimit(responses[promptId], path + "." + promptId,
        LIMITS.promptResponseLength, aggregate, "Prompt responses must contain text values.",
        enforceResourceLimits);
      if (!response.ok) {
        return response;
      }
    }
    return success(responses);
  }

  function validateMemory(memory, dictionaryId, aggregate, enforceResourceLimits) {
    var path = "memories." + dictionaryId;
    var expected = [
      "id",
      "title",
      "memoryText",
      "dateText",
      "people",
      "places",
      "themes",
      "sensoryDetails",
      "promptResponses",
      "narrativeText",
      "createdAt",
      "updatedAt"
    ];
    if (!hasExactKeys(memory, expected)) {
      return invalidSchema(path, "Memory fields are missing or unexpected.");
    }
    if (!isUuid(memory.id) || memory.id !== dictionaryId) {
      return invalidSchema(path + ".id", "A memory ID is invalid or does not match its key.");
    }
    var result = validateTextLimit(memory.title, path + ".title",
      LIMITS.memoryTitleLength, aggregate, "Memory text fields must be text.",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextLimit(memory.memoryText, path + ".memoryText",
      LIMITS.memoryTextLength, aggregate, "Memory text fields must be text.",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextLimit(memory.dateText, path + ".dateText",
      LIMITS.dateTextLength, aggregate, "Memory text fields must be text.",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextLimit(memory.sensoryDetails, path + ".sensoryDetails",
      LIMITS.sensoryDetailsLength, aggregate, "Memory text fields must be text.",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextLimit(memory.narrativeText, path + ".narrativeText",
      LIMITS.narrativeTextLength, aggregate, "Memory text fields must be text.",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextList(memory.people, path + ".people", aggregate,
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextList(memory.places, path + ".places", aggregate,
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateTextList(memory.themes, path + ".themes", aggregate,
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    if (!isIsoTimestamp(memory.createdAt) || !isIsoTimestamp(memory.updatedAt)) {
      return invalidSchema(path, "Memory timestamps must be ISO-8601 values.");
    }
    return validatePromptResponses(memory.promptResponses, path + ".promptResponses", aggregate,
      enforceResourceLimits);
  }

  function validateChapter(chapter, index, aggregate, enforceResourceLimits) {
    var path = "chapters[" + index + "]";
    if (!hasExactKeys(chapter, ["id", "title", "createdAt", "updatedAt", "memoryIds"])) {
      return invalidSchema(path, "Chapter fields are missing or unexpected.");
    }
    if (!isUuid(chapter.id)) {
      return invalidSchema(path, "Chapter ID and title are required.");
    }
    var titleResult = validateTextLimit(chapter.title, path + ".title",
      LIMITS.chapterTitleLength, aggregate, "Chapter ID and title are required.",
      enforceResourceLimits);
    if (!titleResult.ok || !chapter.title.trim()) {
      return titleResult.ok ?
        invalidSchema(path, "Chapter ID and title are required.") :
        titleResult;
    }
    if (!isIsoTimestamp(chapter.createdAt) || !isIsoTimestamp(chapter.updatedAt)) {
      return invalidSchema(path, "Chapter timestamps must be ISO-8601 values.");
    }
    var memoryIds = validateUuidList(chapter.memoryIds, path + ".memoryIds",
      enforceResourceLimits);
    if (!memoryIds.ok) {
      return memoryIds;
    }
    return success(chapter);
  }

  function validateCollectionInternal(candidate, enforceResourceLimits) {
    var expected = [
      "schemaVersion",
      "collectionId",
      "revision",
      "createdAt",
      "updatedAt",
      "book",
      "chapters",
      "unassignedMemoryIds",
      "memories",
      "settings"
    ];

    if (!hasExactKeys(candidate, expected)) {
      return invalidSchema("$", "Collection fields are missing or unexpected.");
    }
    if (candidate.schemaVersion !== 1) {
      return failure("UNSUPPORTED_VERSION", "Only collection schema version 1 is supported.");
    }
    if (!isUuid(candidate.collectionId)) {
      return invalidSchema("collectionId", "The collection ID is invalid.");
    }
    if (!Number.isInteger(candidate.revision) || candidate.revision < 0) {
      return invalidSchema("revision", "The collection revision is invalid.");
    }
    if (!isIsoTimestamp(candidate.createdAt) || !isIsoTimestamp(candidate.updatedAt)) {
      return invalidSchema("$", "Collection timestamps must be ISO-8601 values.");
    }

    var aggregate = enforceResourceLimits ? { total: 0 } : null;
    var result = validateBook(candidate.book, aggregate, enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    if (!Array.isArray(candidate.chapters)) {
      return invalidSchema("chapters", "Chapters must be a list.");
    }
    result = validateCountLimit("chapters", candidate.chapters.length, LIMITS.chapters,
      "The backup exceeds the safe restore chapter count.", enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    result = validateUuidList(candidate.unassignedMemoryIds, "unassignedMemoryIds",
      enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    if (!isObject(candidate.memories)) {
      return invalidSchema("memories", "Memories must be an ID-keyed object.");
    }
    if (!hasExactKeys(candidate.settings, ["backupReminderDismissedAt"])) {
      return invalidSchema("settings", "Settings fields are missing or unexpected.");
    }
    if (candidate.settings.backupReminderDismissedAt !== null &&
        !isIsoTimestamp(candidate.settings.backupReminderDismissedAt)) {
      return invalidSchema("settings.backupReminderDismissedAt",
        "The backup reminder timestamp is invalid.");
    }

    var chapterIds = {};
    var referenceIds = {};
    var duplicateReference = null;
    for (var chapterIndex = 0; chapterIndex < candidate.chapters.length; chapterIndex += 1) {
      var chapter = candidate.chapters[chapterIndex];
      result = validateChapter(chapter, chapterIndex, aggregate, enforceResourceLimits);
      if (!result.ok) {
        return result;
      }
      if (chapterIds[chapter.id]) {
        return failure("DUPLICATE_ID", "A chapter ID appears more than once.", {
          id: chapter.id
        });
      }
      chapterIds[chapter.id] = true;
      chapter.memoryIds.forEach(function (memoryId) {
        if (referenceIds[memoryId]) {
          duplicateReference = memoryId;
        }
        referenceIds[memoryId] = true;
      });
    }
    if (duplicateReference) {
      return failure("DUPLICATE_ID", "A memory is ordered more than once.", {
        id: duplicateReference
      });
    }

    candidate.unassignedMemoryIds.forEach(function (memoryId) {
      if (referenceIds[memoryId]) {
        duplicateReference = memoryId;
      }
      referenceIds[memoryId] = true;
    });
    if (duplicateReference) {
      return failure("DUPLICATE_ID", "A memory is ordered more than once.", {
        id: duplicateReference
      });
    }

    var memoryIds = Object.keys(candidate.memories);
    result = validateCountLimit("memories", memoryIds.length, LIMITS.memories,
      "The backup exceeds the safe restore memory count.", enforceResourceLimits);
    if (!result.ok) {
      return result;
    }
    var entityIds = {};
    for (var memoryIndex = 0; memoryIndex < memoryIds.length; memoryIndex += 1) {
      var memoryId = memoryIds[memoryIndex];
      result = validateMemory(candidate.memories[memoryId], memoryId, aggregate,
        enforceResourceLimits);
      if (!result.ok) {
        return result;
      }
      if (entityIds[candidate.memories[memoryId].id]) {
        return failure("DUPLICATE_ID", "A memory ID appears more than once.", {
          id: candidate.memories[memoryId].id
        });
      }
      entityIds[candidate.memories[memoryId].id] = true;
      if (!referenceIds[memoryId]) {
        return failure("INVALID_REFERENCE", "A memory has no ordering reference.", {
          id: memoryId
        });
      }
    }

    var referencedIds = Object.keys(referenceIds);
    for (var referenceIndex = 0; referenceIndex < referencedIds.length; referenceIndex += 1) {
      if (!Object.prototype.hasOwnProperty.call(candidate.memories, referencedIds[referenceIndex])) {
        return failure("INVALID_REFERENCE", "An ordered memory reference does not exist.", {
          id: referencedIds[referenceIndex]
        });
      }
    }

    return success(clone(candidate));
  }

  function validateCollection(candidate) {
    // Local snapshots and exports keep schema/invariant checks without import-only caps.
    return validateCollectionInternal(candidate, false);
  }

  function validateRestoreCollection(candidate) {
    // Imported bytes are untrusted and must pass resource limits before cloning or rendering.
    return validateCollectionInternal(candidate, true);
  }

  function validateBackup(candidate, byteLength) {
    if (typeof byteLength !== "number" || byteLength < 0) {
      return invalidSchema("sourceByteLength", "Backup byte length is invalid.");
    }
    if (byteLength > MAX_BACKUP_BYTES) {
      return failure("FILE_TOO_LARGE", "The backup exceeds the 10 MiB restore limit.", {
        maximumBytes: MAX_BACKUP_BYTES,
        actualBytes: byteLength
      });
    }
    if (!hasExactKeys(candidate,
      ["format", "formatVersion", "exportedAt", "appVersion", "collection"])) {
      return failure("WRONG_FORMAT", "This file is not a Life Memoir backup.");
    }
    if (candidate.format !== "dreamer.life-memoir.backup") {
      return failure("WRONG_FORMAT", "This file is not a Life Memoir backup.");
    }
    if (candidate.formatVersion !== 1) {
      return failure("UNSUPPORTED_VERSION", "This backup version is not supported.", {
        supportedVersion: 1,
        actualVersion: candidate.formatVersion
      });
    }
    if (!isIsoTimestamp(candidate.exportedAt) ||
        typeof candidate.appVersion !== "string" || !candidate.appVersion) {
      return invalidSchema("$", "Backup metadata is invalid.");
    }

    var collectionResult = validateRestoreCollection(candidate.collection);
    if (!collectionResult.ok) {
      return collectionResult;
    }
    return success({
      collection: collectionResult.value,
      memoryCount: Object.keys(collectionResult.value.memories).length,
      chapterCount: collectionResult.value.chapters.length,
      exportedAt: candidate.exportedAt,
      appVersion: candidate.appVersion,
      sourceByteLength: byteLength
    });
  }

  BookWriter.Validation = Object.freeze({
    MAX_BACKUP_BYTES: MAX_BACKUP_BYTES,
    LIMITS: LIMITS,
    validateCollection: validateCollection,
    validateBackup: validateBackup
  });
}(window.BookWriter));
