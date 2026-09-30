(function (BookWriter, document, window) {
  "use strict";

  var tests = [];
  var IDS = {
    collection: "10000000-0000-4000-8000-000000000001",
    memory1: "20000000-0000-4000-8000-000000000001",
    memory2: "20000000-0000-4000-8000-000000000002",
    memory3: "20000000-0000-4000-8000-000000000003",
    chapter1: "30000000-0000-4000-8000-000000000001",
    chapter2: "30000000-0000-4000-8000-000000000002"
  };
  var TIME = "2026-08-12T20:00:00.000Z";
  var LIMITS = {
    chapters: 50,
    memories: 500,
    listCount: 100,
    promptCount: BookWriter.Validation.LIMITS.promptCount,
    aggregateCharacters: 600000
  };

  function test(name, callback) {
    tests.push({ name: name, callback: callback });
  }

  function clone(value) {
    return JSON.parse(JSON.stringify(value));
  }

  function stubUi(options) {
    var settings = options || {};
    return {
      log: {
        transientClears: 0,
        renders: 0,
        saveStatuses: [],
        storageErrors: [],
        restoreStatuses: [],
        restorePreviews: 0
      },
      ui: {
        render: function () {
          this.__log.renders += 1;
        },
        renderBookPreview: function () {},
        setSaveStatus: function (status, detail) {
          this.__log.saveStatuses.push({ status: status, detail: detail });
        },
        setStorageError: function (error) {
          this.__log.storageErrors.push(error);
        },
        setRestoreStatus: function (message, isError) {
          this.__log.restoreStatuses.push({ message: message, isError: !!isError });
        },
        showConfirm: function () {
          return Promise.resolve(settings.confirm !== false);
        },
        showRestorePreview: function () {
          this.__log.restorePreviews += 1;
          return Promise.resolve(settings.restore !== false);
        },
        clearTransientState: function () {
          this.__log.transientClears += 1;
        },
        __log: null
      }
    };
  }

  function withStubbedUi(stub, callback) {
    var original = BookWriter.UI;
    stub.ui.__log = stub.log;
    BookWriter.UI = stub.ui;
    return Promise.resolve().then(callback).then(function (value) {
      BookWriter.UI = original;
      return value;
    }, function (error) {
      BookWriter.UI = original;
      throw error;
    });
  }

  function assert(condition, message) {
    if (!condition) {
      throw new Error(message || "Expected condition to be true.");
    }
  }

  function exportCollection() {
    var collection = sampleCollection();
    collection.book = {
      title: "A <script> memory book",
      subtitle: "Two lines\nof front matter",
      authorName: "José & family",
      dedication: "For Amma <always>",
      preface: "Preface line 1.\n\nPreface line 2."
    };
    collection.memories[IDS.memory1] = {
      id: IDS.memory1,
      title: "A <script> memory — 東京",
      memoryText: "Source paragraph one.\n\n<img src=x onerror=alert(1)> paragraph two.",
      dateText: "Around <1991>",
      people: ["Amma", "José"],
      places: ["Seattle", "घर"],
      themes: ["Family", "Journey"],
      sensoryDetails: "Rain & cardamom",
      promptResponses: {
        context: "Context <scene>",
        people: "People remembered",
        setting: "Setting remembered",
        senses: "Smell of tea",
        emotions: "A little afraid",
        significance: "It changed me",
        beforeAfter: "Before and after details"
      },
      narrativeText: "Narrative <b>bold</b>\n\nSecond paragraph.",
      createdAt: TIME,
      updatedAt: "2026-08-12T20:05:00.000Z"
    };
    return collection;
  }

  function createCollectionWithMemories(count) {
    var collection = BookWriter.Domain.createEmptyCollection(TIME, idFactories());
    var memoryIndex;
    for (memoryIndex = 0; memoryIndex < count; memoryIndex += 1) {
      var memoryId = numberedUuid(memoryIndex + 1, "20");
      collection.memories[memoryId] = {
        id: memoryId,
        title: "Memory " + memoryIndex,
        memoryText: "Text",
        dateText: "",
        people: [],
        places: [],
        themes: [],
        sensoryDetails: "",
        promptResponses: promptResponses(),
        narrativeText: "",
        createdAt: TIME,
        updatedAt: TIME
      };
      collection.unassignedMemoryIds.push(memoryId);
    }
    return collection;
  }

  function createCollectionBeyondRestoreLimits() {
    var collection = createCollectionWithMemories(LIMITS.memories + 1);
    var chapterIndex;
    for (chapterIndex = 0; chapterIndex < LIMITS.chapters + 1; chapterIndex += 1) {
      collection.chapters.push({
        id: numberedUuid(chapterIndex + 1, "30"),
        title: "Chapter " + chapterIndex,
        createdAt: TIME,
        updatedAt: TIME,
        memoryIds: []
      });
    }
    collection.memories[numberedUuid(1, "20")].memoryText =
      new Array(BookWriter.Validation.LIMITS.memoryTextLength + 2).join("x");
    return collection;
  }

  function backupFileForCollection(collection, fileName) {
    return new File([JSON.stringify({
      format: "dreamer.life-memoir.backup",
      formatVersion: 1,
      exportedAt: TIME,
      appVersion: "1.0.0",
      collection: collection
    })], fileName, {
      type: "application/json"
    });
  }

  function equal(actual, expected, message) {
    if (actual !== expected) {
      throw new Error((message || "Values differ.") +
        " Expected " + JSON.stringify(expected) + " but got " + JSON.stringify(actual) + ".");
    }
  }

  function deepEqual(actual, expected, message) {
    equal(JSON.stringify(actual), JSON.stringify(expected), message);
  }

  function resultValue(result) {
    if (!result.ok) {
      throw new Error(result.error.code + ": " + result.error.message);
    }
    return result.value;
  }

  function idFactories() {
    return {
      collectionId: function () { return IDS.collection; },
      memoryId: function () { return IDS.memory1; },
      chapterId: function () { return IDS.chapter1; }
    };
  }

  function apply(collection, command) {
    return resultValue(BookWriter.Domain.apply(collection, command, TIME));
  }

  function sampleCollection() {
    var collection = BookWriter.Domain.createEmptyCollection(TIME, idFactories());
    collection = apply(collection, {
      type: "CREATE_MEMORY",
      id: IDS.memory1,
      fields: {
        title: "Mañana by the sea",
        memoryText: "First paragraph.\n\nSecond paragraph with 東京.",
        dateText: "Sometime in the 1980s",
        people: ["Amma"],
        places: ["Seattle"],
        themes: ["Family"],
        sensoryDetails: "Salt air"
      }
    });
    collection = apply(collection, {
      type: "CREATE_MEMORY",
      id: IDS.memory2,
      fields: {
        title: "School day",
        memoryText: "A blue classroom.",
        people: ["Mr. Lee"],
        places: ["School"],
        themes: ["Learning"],
        sensoryDetails: "Chalk dust"
      }
    });
    collection = apply(collection, {
      type: "CREATE_CHAPTER",
      id: IDS.chapter1,
      title: "Beginnings"
    });
    collection = apply(collection, {
      type: "MOVE_MEMORY",
      memoryId: IDS.memory1,
      targetChapterId: IDS.chapter1,
      targetIndex: 0
    });
    return collection;
  }

  function fakeStorage(initial) {
    var data = initial || {};
    return {
      setCalls: 0,
      removeCalls: 0,
      failGet: null,
      failSet: null,
      failRemove: null,
      getItem: function (key) {
        if (this.failGet) {
          throw this.failGet;
        }
        return Object.prototype.hasOwnProperty.call(data, key) ? data[key] : null;
      },
      setItem: function (key, value) {
        if (this.failSet) {
          throw this.failSet;
        }
        this.setCalls += 1;
        data[key] = String(value);
      },
      removeItem: function (key) {
        if (this.failRemove) {
          throw this.failRemove;
        }
        this.removeCalls += 1;
        delete data[key];
      },
      raw: function (key) {
        return Object.prototype.hasOwnProperty.call(data, key) ? data[key] : null;
      }
    };
  }

  function byteLength(text) {
    return new TextEncoder().encode(text).length;
  }

  function promptResponses() {
    return {
      context: "",
      people: "",
      setting: "",
      senses: "",
      emotions: "",
      significance: "",
      beforeAfter: ""
    };
  }

  function numberedUuid(number, family) {
    var tail = number.toString(16).padStart(12, "0");
    return family + "000000-0000-4000-8000-" + tail;
  }

  function performanceCollection() {
    var chapters = [];
    var memories = {};
    var repeated = new Array(994).join("x");
    for (var chapterIndex = 0; chapterIndex < 50; chapterIndex += 1) {
      chapters.push({
        id: numberedUuid(chapterIndex + 1, "30"),
        title: "Chapter " + (chapterIndex + 1),
        createdAt: TIME,
        updatedAt: TIME,
        memoryIds: []
      });
    }
    for (var memoryIndex = 0; memoryIndex < 500; memoryIndex += 1) {
      var memoryId = numberedUuid(memoryIndex + 1, "20");
      memories[memoryId] = {
        id: memoryId,
        title: memoryIndex === 499 ? "needle" : "Memory " + memoryIndex,
        memoryText: repeated,
        dateText: "",
        people: [],
        places: [],
        themes: ["Theme"],
        sensoryDetails: "",
        promptResponses: promptResponses(),
        narrativeText: "",
        createdAt: TIME,
        updatedAt: TIME
      };
      chapters[memoryIndex % 50].memoryIds.push(memoryId);
    }
    return {
      schemaVersion: 1,
      collectionId: IDS.collection,
      revision: 0,
      createdAt: TIME,
      updatedAt: TIME,
      book: {
        title: "Performance memoir",
        subtitle: "",
        authorName: "",
        dedication: "",
        preface: ""
      },
      chapters: chapters,
      unassignedMemoryIds: [],
      memories: memories,
      settings: { backupReminderDismissedAt: null }
    };
  }

  test("foundation: namespace exposes all frozen components", function () {
    ["Domain", "Validation", "Persistence", "Prompts", "Backup", "Export", "UI", "App"]
      .forEach(function (name) {
        assert(BookWriter[name], name + " is missing.");
      });
  });

  test("domain: empty collection matches schema v1", function () {
    var collection = BookWriter.Domain.createEmptyCollection(TIME, idFactories());
    equal(collection.schemaVersion, 1);
    equal(collection.revision, 0);
    equal(collection.collectionId, IDS.collection);
    deepEqual(collection.unassignedMemoryIds, []);
  });

  test("domain: create and update preserve source Unicode and imprecise dates", function () {
    var collection = sampleCollection();
    equal(collection.memories[IDS.memory1].dateText, "Sometime in the 1980s");
    assert(collection.memories[IDS.memory1].memoryText.indexOf("東京") !== -1);
    equal(collection.memories[IDS.memory1].createdAt, TIME);
  });

  test("search: required fields and filters return stable ordered matches", function () {
    var collection = sampleCollection();
    equal(BookWriter.Domain.search(collection, "mañana", {})[0], IDS.memory1);
    equal(BookWriter.Domain.search(collection, "blue classroom", {})[0], IDS.memory2);
    equal(BookWriter.Domain.search(collection, "amma", {})[0], IDS.memory1);
    equal(BookWriter.Domain.search(collection, "school", {})[0], IDS.memory2);
    equal(BookWriter.Domain.search(collection, "family", {})[0], IDS.memory1);
    deepEqual(BookWriter.Domain.search(collection, "", { chapterId: null }), [IDS.memory2]);
    deepEqual(BookWriter.Domain.search(collection, "", { theme: "Learning" }), [IDS.memory2]);
  });

  test("ordering: moving memories and chapters is deterministic", function () {
    var collection = sampleCollection();
    collection = apply(collection, {
      type: "CREATE_CHAPTER",
      id: IDS.chapter2,
      title: "Later"
    });
    collection = apply(collection, {
      type: "MOVE_CHAPTER",
      chapterId: IDS.chapter2,
      direction: "up"
    });
    equal(collection.chapters[0].id, IDS.chapter2);
    collection = apply(collection, {
      type: "MOVE_MEMORY",
      memoryId: IDS.memory2,
      targetChapterId: IDS.chapter1,
      targetIndex: 0
    });
    deepEqual(collection.chapters[1].memoryIds, [IDS.memory2, IDS.memory1]);
  });

  test("ordering: same-list moves persist exactly across reload", function () {
    var collection = sampleCollection();
    collection = apply(collection, {
      type: "MOVE_MEMORY",
      memoryId: IDS.memory2,
      targetChapterId: IDS.chapter1,
      targetIndex: 1
    });
    collection = apply(collection, {
      type: "CREATE_MEMORY",
      id: IDS.memory3,
      fields: { title: "Third" }
    });
    collection = apply(collection, {
      type: "MOVE_MEMORY",
      memoryId: IDS.memory3,
      targetChapterId: IDS.chapter1,
      targetIndex: 2
    });
    collection = apply(collection, {
      type: "MOVE_MEMORY",
      memoryId: IDS.memory1,
      targetChapterId: IDS.chapter1,
      targetIndex: 1
    });
    deepEqual(collection.chapters[0].memoryIds,
      [IDS.memory2, IDS.memory1, IDS.memory3]);

    var storage = fakeStorage();
    BookWriter.Persistence._setStorageForTests(storage);
    assert(BookWriter.Persistence.save(collection, 0).ok);
    var loaded = resultValue(BookWriter.Persistence.load());
    deepEqual(loaded.chapters[0].memoryIds,
      [IDS.memory2, IDS.memory1, IDS.memory3]);
  });

  test("domain: invalid input fails without mutating the caller", function () {
    var collection = sampleCollection();
    var before = JSON.stringify(collection);
    var result = BookWriter.Domain.apply(collection, {
      type: "MOVE_MEMORY",
      memoryId: IDS.memory1,
      targetChapterId: IDS.chapter1,
      targetIndex: -1
    }, TIME);
    equal(result.error.code, "INVALID_SCHEMA");
    equal(JSON.stringify(collection), before);
  });

  test("chapter: confirmed deletion preserves memories as unassigned", function () {
    var collection = sampleCollection();
    var before = JSON.stringify(collection);
    var canceled = BookWriter.Domain.apply(collection, {
      type: "DELETE_CHAPTER",
      chapterId: IDS.chapter1,
      confirmationToken: "NO"
    }, TIME);
    assert(!canceled.ok);
    equal(JSON.stringify(collection), before, "Canceled command mutated input.");
    collection = apply(collection, {
      type: "DELETE_CHAPTER",
      chapterId: IDS.chapter1,
      confirmationToken: "DELETE_CHAPTER"
    });
    equal(collection.chapters.length, 0);
    deepEqual(collection.unassignedMemoryIds, [IDS.memory2, IDS.memory1]);
    assert(collection.memories[IDS.memory1]);
  });

  test("prompt: catalogue is respectful, optional, and immutable", function () {
    equal(BookWriter.Prompts.catalogue.length, 7);
    assert(Object.isFrozen(BookWriter.Prompts.catalogue));
    assert(BookWriter.Prompts.catalogue.every(function (prompt) {
      return /if|would|want|like|any/i.test(prompt.text);
    }), "Prompts should use optional language.");
  });

  test("book: narrative fallback never overwrites source memory", function () {
    var collection = sampleCollection();
    var source = collection.memories[IDS.memory1].memoryText;
    var sections = BookWriter.Domain.assembleBook(collection);
    assert(sections[1].paragraphs[0].indexOf(source) !== -1);
    collection = apply(collection, {
      type: "UPDATE_NARRATIVE",
      memoryId: IDS.memory1,
      text: "Edited narrative."
    });
    sections = BookWriter.Domain.assembleBook(collection);
    assert(sections[1].paragraphs[0].indexOf("Edited narrative.") !== -1);
    equal(collection.memories[IDS.memory1].memoryText, source);
  });

  test("book: export assembly preserves every supported memory field", function () {
    var collection = exportCollection();
    var sections = BookWriter.Domain.assembleBook(collection, {
      includeStructuredFields: true
    });
    var memory = sections[1].memories[0];
    equal(memory.title, collection.memories[IDS.memory1].title);
    equal(memory.dateText, collection.memories[IDS.memory1].dateText);
    equal(memory.memoryText, collection.memories[IDS.memory1].memoryText);
    equal(memory.narrativeText, collection.memories[IDS.memory1].narrativeText);
    deepEqual(memory.people, collection.memories[IDS.memory1].people);
    deepEqual(memory.places, collection.memories[IDS.memory1].places);
    deepEqual(memory.themes, collection.memories[IDS.memory1].themes);
    equal(memory.sensoryDetails, collection.memories[IDS.memory1].sensoryDetails);
    deepEqual(memory.promptResponses, collection.memories[IDS.memory1].promptResponses);
    equal(memory.createdAt, TIME);
    equal(memory.updatedAt, "2026-08-12T20:05:00.000Z");
  });

  test("validation: valid collection returns a detached clone", function () {
    var collection = sampleCollection();
    var result = BookWriter.Validation.validateCollection(collection);
    assert(result.ok);
    assert(result.value !== collection);
    result.value.book.title = "Changed";
    equal(collection.book.title, "");
  });

  test("validation: missing required fields are rejected", function () {
    var collection = sampleCollection();
    delete collection.book.preface;
    var result = BookWriter.Validation.validateCollection(collection);
    equal(result.error.code, "INVALID_SCHEMA");
  });

  test("validation: duplicate ordering references are rejected", function () {
    var collection = sampleCollection();
    collection.unassignedMemoryIds.push(IDS.memory1);
    var result = BookWriter.Validation.validateCollection(collection);
    equal(result.error.code, "DUPLICATE_ID");
  });

  test("validation: dangling and unreferenced memories are rejected", function () {
    var collection = sampleCollection();
    collection.chapters[0].memoryIds[0] = IDS.memory3;
    var result = BookWriter.Validation.validateCollection(collection);
    equal(result.error.code, "INVALID_REFERENCE");
  });

  test("validation: duplicate chapter IDs are rejected", function () {
    var collection = sampleCollection();
    collection.chapters.push(JSON.parse(JSON.stringify(collection.chapters[0])));
    var result = BookWriter.Validation.validateCollection(collection);
    equal(result.error.code, "DUPLICATE_ID");
  });

  test("validation: backup format, version, and size use stable errors", function () {
    var collection = sampleCollection();
    var base = {
      format: "dreamer.life-memoir.backup",
      formatVersion: 1,
      exportedAt: TIME,
      appVersion: "1.0.0",
      collection: collection
    };
    var wrong = JSON.parse(JSON.stringify(base));
    wrong.format = "other";
    equal(BookWriter.Validation.validateBackup(wrong, 10).error.code, "WRONG_FORMAT");
    var future = JSON.parse(JSON.stringify(base));
    future.formatVersion = 2;
    equal(BookWriter.Validation.validateBackup(future, 10).error.code, "UNSUPPORTED_VERSION");
    equal(BookWriter.Validation.validateBackup(base,
      BookWriter.Validation.MAX_BACKUP_BYTES + 1).error.code, "FILE_TOO_LARGE");
  });

  test("persistence: save uses one write, advances revision, and loads fidelity", function () {
    var storage = fakeStorage();
    BookWriter.Persistence._setStorageForTests(storage);
    var collection = sampleCollection();
    var saved = BookWriter.Persistence.save(collection, 0);
    assert(saved.ok);
    equal(storage.setCalls, 1);
    equal(saved.value.revision, 1);
    equal(collection.revision, 0, "Caller collection must not be mutated.");
    var loaded = resultValue(BookWriter.Persistence.load());
    equal(loaded.revision, 1);
    equal(loaded.memories[IDS.memory1].memoryText,
      collection.memories[IDS.memory1].memoryText);
  });

  test("local data: restore limits do not constrain persistence or backup fidelity", function () {
    var storage = fakeStorage();
    var collection = createCollectionBeyondRestoreLimits();
    var longMemoryId = numberedUuid(1, "20");
    BookWriter.Persistence._setStorageForTests(storage);

    var saved = BookWriter.Persistence.save(collection, 0);
    assert(saved.ok, saved.error && saved.error.message);
    equal(storage.setCalls, 1);
    var stored = resultValue(BookWriter.Persistence.load());
    equal(Object.keys(stored.memories).length, LIMITS.memories + 1);
    equal(stored.chapters.length, LIMITS.chapters + 1);
    equal(stored.memories[longMemoryId].memoryText,
      collection.memories[longMemoryId].memoryText);

    var serialized = BookWriter.Backup.serialize(collection, "1.0.0", TIME);
    var wrapper = JSON.parse(serialized);
    deepEqual(wrapper.collection, collection,
      "Backup serialization must preserve the complete oversized local collection.");
    BookWriter.Persistence._setStorageForTests(null);
  });

  test("restore: an oversized local backup is rejected before mutation or rendering", function () {
    var current = sampleCollection();
    var currentSnapshot = JSON.stringify(current);
    var data = {};
    data[BookWriter.Persistence.STORAGE_KEY] = currentSnapshot;
    var storage = fakeStorage(data);
    var oversizedJson = BookWriter.Backup.serialize(
      createCollectionBeyondRestoreLimits(), "1.0.0", TIME);
    var file = new File([oversizedJson], "oversized-local-backup.json", {
      type: "application/json"
    });
    var stub = stubUi();

    return withStubbedUi(stub, function () {
      BookWriter.Persistence._setStorageForTests(storage);
      BookWriter.App._test.resetState({
        collection: clone(current),
        selectedMemoryId: IDS.memory1,
        firstRun: false
      });
      return BookWriter.App._test.restoreFile(file);
    }).then(function () {
      var lastStatus = stub.log.restoreStatuses[stub.log.restoreStatuses.length - 1];
      equal(lastStatus.isError, true);
      assert(lastStatus.message.indexOf("INVALID_SCHEMA") !== -1);
      assert(lastStatus.message.indexOf("Split a very large collection") !== -1);
      equal(stub.log.restorePreviews, 0);
      equal(stub.log.renders, 0);
      equal(storage.raw(BookWriter.Persistence.STORAGE_KEY), currentSnapshot);
      equal(storage.setCalls, 0);
      equal(storage.removeCalls, 0);
      deepEqual(BookWriter.App._test.getState().collection, current);
      BookWriter.Persistence._setStorageForTests(null);
      BookWriter.App._test.resetState();
    });
  });

  test("persistence: malformed stored bytes block save without replacement", function () {
    var data = {};
    data[BookWriter.Persistence.STORAGE_KEY] = "{not-json";
    var storage = fakeStorage(data);
    BookWriter.Persistence._setStorageForTests(storage);
    var result = BookWriter.Persistence.save(sampleCollection(), 0);
    equal(result.error.code, "INVALID_JSON");
    equal(storage.setCalls, 0);
    equal(storage.raw(BookWriter.Persistence.STORAGE_KEY), "{not-json");
  });

  test("storage-failure: quota failure retains caller revision and reports actionably", function () {
    var storage = fakeStorage();
    var quota = new Error("full");
    quota.name = "QuotaExceededError";
    storage.failSet = quota;
    BookWriter.Persistence._setStorageForTests(storage);
    var collection = sampleCollection();
    var result = BookWriter.Persistence.save(collection, 0);
    equal(result.error.code, "STORAGE_QUOTA");
    equal(collection.revision, 0);
    equal(storage.setCalls, 0);
  });

  test("storage-failure: unavailable storage is normalized", function () {
    var storage = fakeStorage();
    storage.failGet = new Error("blocked");
    BookWriter.Persistence._setStorageForTests(storage);
    equal(BookWriter.Persistence.load().error.code, "STORAGE_UNAVAILABLE");
  });

  test("stale-revision: a newer stored revision blocks overwrite", function () {
    var storedCollection = sampleCollection();
    storedCollection.revision = 2;
    var data = {};
    data[BookWriter.Persistence.STORAGE_KEY] = JSON.stringify(storedCollection);
    var storage = fakeStorage(data);
    BookWriter.Persistence._setStorageForTests(storage);
    var draft = sampleCollection();
    draft.revision = 1;
    var result = BookWriter.Persistence.save(draft, 1);
    equal(result.error.code, "STALE_REVISION");
    equal(storage.setCalls, 0);
  });

  test("persistence: erase removes only the application key", function () {
    var collection = sampleCollection();
    collection.revision = 3;
    var data = { unrelated: "keep" };
    data[BookWriter.Persistence.STORAGE_KEY] = JSON.stringify(collection);
    var storage = fakeStorage(data);
    BookWriter.Persistence._setStorageForTests(storage);
    assert(BookWriter.Persistence.erase(3).ok);
    equal(storage.raw(BookWriter.Persistence.STORAGE_KEY), null);
    equal(storage.raw("unrelated"), "keep");
  });

  test("persistence: failed erase preserves the application and unrelated keys", function () {
    var collection = sampleCollection();
    collection.revision = 3;
    var data = { unrelated: "keep" };
    data[BookWriter.Persistence.STORAGE_KEY] = JSON.stringify(collection);
    var storage = fakeStorage(data);
    storage.failRemove = new Error("blocked");
    BookWriter.Persistence._setStorageForTests(storage);
    var result = BookWriter.Persistence.erase(3);
    equal(result.error.code, "STORAGE_UNAVAILABLE");
    assert(storage.raw(BookWriter.Persistence.STORAGE_KEY) !== null);
    equal(storage.raw("unrelated"), "keep");
  });

  test("persistence: storage event subscribers receive only the app key", function () {
    var count = 0;
    var unsubscribe = BookWriter.Persistence.subscribeExternalChange(function () {
      count += 1;
    });
    window.dispatchEvent(new StorageEvent("storage", { key: "other" }));
    window.dispatchEvent(new StorageEvent("storage", {
      key: BookWriter.Persistence.STORAGE_KEY
    }));
    unsubscribe();
    equal(count, 1);
  });

  test("backup: serialize, inspect, and commit preserve complete data", function () {
    var collection = sampleCollection();
    var json = BookWriter.Backup.serialize(collection, "1.0.0", TIME);
    var file = new File([json], "backup.json", { type: "application/json" });
    return BookWriter.Backup.inspect(file).then(function (result) {
      var preview = resultValue(result);
      equal(preview.memoryCount, 2);
      equal(preview.chapterCount, 1);
      var prepared = resultValue(BookWriter.Backup.commit(preview, 9));
      equal(prepared.revision, 9);
      equal(prepared.memories[IDS.memory1].memoryText,
        collection.memories[IDS.memory1].memoryText);
      prepared.book.title = "Detached";
      equal(preview.collection.book.title, "");
    });
  });

  test("backup: round trip preserves every supported field and ordering", function () {
    var collection = sampleCollection();
    collection.book = {
      title: "जीवन — 東京",
      subtitle: "Line one\nLine two",
      authorName: "José",
      dedication: "For Amma",
      preface: "Before\n\nAfter"
    };
    collection.memories[IDS.memory1].promptResponses.context = "Context ✓";
    collection.memories[IDS.memory1].narrativeText = "Narrative\n\n段落";
    collection.settings.backupReminderDismissedAt = TIME;
    var original = JSON.stringify(collection);
    var json = BookWriter.Backup.serialize(collection, "1.0.0", TIME);
    var file = new File([json], "backup.json", { type: "application/json" });
    return BookWriter.Backup.inspect(file).then(function (result) {
      var preview = resultValue(result);
      equal(JSON.stringify(preview.collection), original);
      var prepared = resultValue(BookWriter.Backup.commit(preview, 7));
      prepared.revision = collection.revision;
      equal(JSON.stringify(prepared), original);
    });
  });

  test("restore: malformed, wrong-type, and oversized files change no storage", function () {
    var storage = fakeStorage();
    BookWriter.Persistence._setStorageForTests(storage);
    var malformed = new File(["{"], "bad.json", { type: "application/json" });
    return BookWriter.Backup.inspect(malformed).then(function (result) {
      equal(result.error.code, "INVALID_JSON");
      return BookWriter.Backup.inspect(new File(["{}"], "bad.txt", {
        type: "text/plain"
      }));
    }).then(function (result) {
      equal(result.error.code, "WRONG_FORMAT");
      return BookWriter.Backup.inspect({
        size: BookWriter.Validation.MAX_BACKUP_BYTES + 1,
        type: "application/json"
      });
    }).then(function (result) {
      equal(result.error.code, "FILE_TOO_LARGE");
      equal(storage.setCalls, 0);
    });
  });

  test("restore: confirmed preparation requires exactly one persistence write", function () {
    var collection = sampleCollection();
    var wrapper = JSON.parse(BookWriter.Backup.serialize(collection, "1.0.0", TIME));
    var preview = resultValue(BookWriter.Validation.validateBackup(wrapper,
      byteLength(JSON.stringify(wrapper))));
    var prepared = resultValue(BookWriter.Backup.commit(preview, 0));
    var storage = fakeStorage();
    BookWriter.Persistence._setStorageForTests(storage);
    assert(BookWriter.Persistence.save(prepared, 0).ok);
    equal(storage.setCalls, 1);
  });

  test("restore: failed replacement write is atomic and preserves existing bytes", function () {
    var existing = sampleCollection();
    existing.revision = 4;
    var data = {};
    data[BookWriter.Persistence.STORAGE_KEY] = JSON.stringify(existing);
    var storage = fakeStorage(data);
    BookWriter.Persistence._setStorageForTests(storage);

    var replacement = sampleCollection();
    replacement.book.title = "Replacement";
    var wrapper = JSON.parse(BookWriter.Backup.serialize(
      replacement, "1.0.0", TIME));
    var preview = resultValue(BookWriter.Validation.validateBackup(
      wrapper, byteLength(JSON.stringify(wrapper))));
    var prepared = resultValue(BookWriter.Backup.commit(preview, 4));
    var before = storage.raw(BookWriter.Persistence.STORAGE_KEY);
    var quota = new Error("full");
    quota.name = "QuotaExceededError";
    storage.failSet = quota;

    var result = BookWriter.Persistence.save(prepared, 4);
    equal(result.error.code, "STORAGE_QUOTA");
    equal(storage.raw(BookWriter.Persistence.STORAGE_KEY), before);
    equal(existing.book.title, "");
    equal(preview.collection.book.title, "Replacement");
  });

  test("restore: unsupported and invalid-reference backups never touch storage", function () {
    var storage = fakeStorage();
    BookWriter.Persistence._setStorageForTests(storage);
    var wrapper = JSON.parse(BookWriter.Backup.serialize(
      sampleCollection(), "1.0.0", TIME));
    wrapper.formatVersion = 2;
    var unsupported = new File([JSON.stringify(wrapper)], "future.json", {
      type: "application/json"
    });
    return BookWriter.Backup.inspect(unsupported).then(function (result) {
      equal(result.error.code, "UNSUPPORTED_VERSION");
      wrapper.formatVersion = 1;
      wrapper.collection.chapters[0].memoryIds[0] = IDS.memory3;
      return BookWriter.Backup.inspect(new File(
        [JSON.stringify(wrapper)], "dangling.json", {
          type: "application/json"
        }));
    }).then(function (result) {
      equal(result.error.code, "INVALID_REFERENCE");
      equal(storage.setCalls, 0);
      equal(storage.removeCalls, 0);
    });
  });

  test("restore: structured chapter and memory limits are rejected before mutation", function () {
    var storage = fakeStorage();
    BookWriter.Persistence._setStorageForTests(storage);

    var tooManyChapters = BookWriter.Domain.createEmptyCollection(TIME, idFactories());
    var chapterIndex;
    for (chapterIndex = 0; chapterIndex < LIMITS.chapters + 1; chapterIndex += 1) {
      tooManyChapters.chapters.push({
        id: numberedUuid(chapterIndex + 1, "30"),
        title: "Chapter " + chapterIndex,
        createdAt: TIME,
        updatedAt: TIME,
        memoryIds: []
      });
    }

    var tooManyMemories = createCollectionWithMemories(LIMITS.memories + 1);

    return BookWriter.Backup.inspect(
      backupFileForCollection(tooManyChapters, "too-many-chapters.json")
    ).then(function (result) {
      equal(result.error.code, "INVALID_SCHEMA");
      equal(storage.setCalls, 0);
      return BookWriter.Backup.inspect(
        backupFileForCollection(tooManyMemories, "too-many-memories.json")
      );
    }).then(function (result) {
      equal(result.error.code, "INVALID_SCHEMA");
      equal(storage.setCalls, 0);
    });
  });

  test("restore: structured list, field, and aggregate limits are rejected before mutation", function () {
    var storage = fakeStorage();
    BookWriter.Persistence._setStorageForTests(storage);

    var tooManyPeople = sampleCollection();
    tooManyPeople.memories[IDS.memory1].people = [];
    for (var personIndex = 0; personIndex < LIMITS.listCount + 1; personIndex += 1) {
      tooManyPeople.memories[IDS.memory1].people.push("Person " + personIndex);
    }

    var fieldTooLong = sampleCollection();
    fieldTooLong.memories[IDS.memory1].title = new Array(302).join("x");

    var aggregateTooLarge = createCollectionWithMemories(7);
    Object.keys(aggregateTooLarge.memories).forEach(function (memoryId) {
      aggregateTooLarge.memories[memoryId].memoryText = new Array(90001).join("x");
    });
    var peopleWrapper = JSON.parse(JSON.stringify({
      format: "dreamer.life-memoir.backup",
      formatVersion: 1,
      exportedAt: TIME,
      appVersion: "1.0.0",
      collection: tooManyPeople
    }));
    var peopleResult = BookWriter.Validation.validateBackup(peopleWrapper,
      byteLength(JSON.stringify(peopleWrapper)));
    equal(peopleResult.error.code, "INVALID_SCHEMA");
    equal(storage.setCalls, 0);

    var fieldWrapper = JSON.parse(JSON.stringify({
      format: "dreamer.life-memoir.backup",
      formatVersion: 1,
      exportedAt: TIME,
      appVersion: "1.0.0",
      collection: fieldTooLong
    }));
    var fieldResult = BookWriter.Validation.validateBackup(fieldWrapper,
      byteLength(JSON.stringify(fieldWrapper)));
    equal(fieldResult.error.code, "INVALID_SCHEMA");
    equal(storage.setCalls, 0);

    var aggregateWrapper = JSON.parse(JSON.stringify({
      format: "dreamer.life-memoir.backup",
      formatVersion: 1,
      exportedAt: TIME,
      appVersion: "1.0.0",
      collection: aggregateTooLarge
    }));
    var aggregateResult = BookWriter.Validation.validateBackup(aggregateWrapper,
      byteLength(JSON.stringify(aggregateWrapper)));
    equal(aggregateResult.error.code, "INVALID_SCHEMA");
    equal(storage.setCalls, 0);
  });

  test("restore: prompt-count overflow is rejected before mutating current state or storage", function () {
    var current = sampleCollection();
    var currentSnapshot = JSON.stringify(current);
    var storage = fakeStorage((function () {
      var data = {};
      data[BookWriter.Persistence.STORAGE_KEY] = currentSnapshot;
      return data;
    }()));
    BookWriter.Persistence._setStorageForTests(storage);
    BookWriter.App._test.resetState({
      collection: clone(current),
      selectedMemoryId: IDS.memory1,
      firstRun: false
    });

    var overflowCollection = sampleCollection();
    overflowCollection.memories[IDS.memory1].promptResponses.extraPrompt = "One too many";
    equal(Object.keys(overflowCollection.memories[IDS.memory1].promptResponses).length,
      LIMITS.promptCount + 1);

    var overflowWrapper = JSON.parse(JSON.stringify({
      format: "dreamer.life-memoir.backup",
      formatVersion: 1,
      exportedAt: TIME,
      appVersion: "1.0.0",
      collection: overflowCollection
    }));
    var overflowResult = BookWriter.Validation.validateBackup(overflowWrapper,
      byteLength(JSON.stringify(overflowWrapper)));
    equal(overflowResult.error.code, "INVALID_SCHEMA");
    equal(overflowResult.error.details.path,
      "memories." + IDS.memory1 + ".promptResponses");
    equal(storage.raw(BookWriter.Persistence.STORAGE_KEY), currentSnapshot);
    equal(storage.setCalls, 0);
    equal(storage.removeCalls, 0);
    deepEqual(BookWriter.App._test.getState().collection, current);
    BookWriter.Persistence._setStorageForTests(null);
    BookWriter.App._test.resetState();
  });

  test("export: HTML is standalone, escaped, scriptless, and restrictive", function () {
    var collection = sampleCollection();
    collection.memories[IDS.memory1].narrativeText =
      "<script>window.evil=true</script><img src=x onerror=alert(1)>\n\n東京";
    var html = BookWriter.Export.toHtml(collection);
    assert(html.indexOf("default-src 'none'") !== -1);
    assert(html.indexOf("&lt;script&gt;") !== -1);
    assert(!/<script[\s>]/i.test(html), "Export must not contain script elements.");
    assert(html.indexOf("<img src=x") === -1);
    assert(html.indexOf("東京") !== -1);
  });

  test("export: text is deterministic and preserves order and Unicode", function () {
    var collection = sampleCollection();
    var first = BookWriter.Export.toText(collection);
    var second = BookWriter.Export.toText(collection);
    equal(first, second);
    assert(first.indexOf("東京") !== -1);
    assert(first.indexOf("Beginnings") < first.indexOf("Unassigned Memories"));
  });

  test("export: HTML and text preserve every supported memory field and escape them", function () {
    var collection = exportCollection();
    var html = BookWriter.Export.toHtml(collection);
    var text = BookWriter.Export.toText(collection);
    assert(html.indexOf("&lt;script&gt; memory") !== -1);
    assert(html.indexOf("&lt;img src=x onerror=alert(1)&gt;") !== -1);
    assert(html.indexOf("Approximate date") !== -1);
    assert(html.indexOf("Source memory") !== -1);
    assert(html.indexOf("Narrative") !== -1);
    assert(html.indexOf("People remembered") !== -1);
    assert(html.indexOf("Rain &amp; cardamom") !== -1);
    assert(html.indexOf("Created") !== -1);
    assert(html.indexOf("Last updated") !== -1);
    assert(html.indexOf("For Amma &lt;always&gt;") !== -1);
    assert(!/<script[\s>]/i.test(html), "Export must not contain script elements.");

    assert(text.indexOf("A <script> memory — 東京") !== -1);
    assert(text.indexOf("Approximate date: Around <1991>") !== -1);
    assert(text.indexOf("People: Amma, José") !== -1);
    assert(text.indexOf("Places: Seattle, घर") !== -1);
    assert(text.indexOf("Themes: Family, Journey") !== -1);
    assert(text.indexOf("Sensory details: Rain & cardamom") !== -1);
    assert(text.indexOf("Source memory") !== -1);
    assert(text.indexOf("Narrative") !== -1);
    assert(text.indexOf("Context\nContext <scene>") !== -1);
    assert(text.indexOf("Created: " + TIME) !== -1);
    assert(text.indexOf("Last updated: 2026-08-12T20:05:00.000Z") !== -1);
    assert(text.indexOf("For Amma <always>") !== -1);
  });

  test("export: empty collection remains standalone and readable", function () {
    var collection = BookWriter.Domain.createEmptyCollection(TIME, idFactories());
    var html = BookWriter.Export.toHtml(collection);
    var text = BookWriter.Export.toText(collection);
    assert(html.indexOf("<h1>My Life Story</h1>") !== -1);
    assert(html.indexOf("default-src 'none'") !== -1);
    equal(text, "My Life Story\n=============\n");
  });

  test("ui: book rendering treats script-shaped text as text nodes", function () {
    var fixture = document.getElementById("ui-test-fixture");
    BookWriter.UI.renderBookSections(fixture, [{
      type: "frontMatter",
      title: "<img src=x>",
      paragraphs: ["<script>window.evil=true</script>"]
    }]);
    equal(fixture.querySelector("script"), null);
    assert(fixture.textContent.indexOf("<script>") !== -1);
    equal(window.evil, undefined);
  });

  test("app: comma-list normalization removes blanks and duplicates", function () {
    deepEqual(BookWriter.App._test.splitList(" Family, travel, family, , 東京 "),
      ["Family", "travel", "東京"]);
  });

  test("app: explicit narrative composition preserves source and includes responses", function () {
    var collection = sampleCollection();
    collection.memories[IDS.memory1].promptResponses.setting = "Beside the open window.";
    var source = collection.memories[IDS.memory1].memoryText;
    var draft = BookWriter.App._test.composeNarrativeText(collection.memories[IDS.memory1]);
    assert(draft.indexOf(source) === 0);
    assert(draft.indexOf("Setting\nBeside the open window.") !== -1);
    equal(collection.memories[IDS.memory1].memoryText, source);
    equal(collection.memories[IDS.memory1].narrativeText, "");
  });

  test("app: creating a memory while filters are active keeps the draft visible", function () {
    var stub = stubUi();
    var storage = fakeStorage();
    var collection = sampleCollection();
    var beforeIds = Object.keys(collection.memories);
    return withStubbedUi(stub, function () {
      BookWriter.Persistence._setStorageForTests(storage);
      BookWriter.App._test.resetState({
        collection: collection,
        selectedMemoryId: IDS.memory1,
        query: "amma",
        filters: {
          chapterId: IDS.chapter1,
          theme: "Family"
        },
        firstRun: false
      });
      BookWriter.App._test.createMemory();
      var after = BookWriter.App._test.getState();
      var newId = Object.keys(after.collection.memories).filter(function (memoryId) {
        return beforeIds.indexOf(memoryId) === -1;
      })[0];
      equal(after.selectedMemoryId, newId);
      equal(after.query, "");
      deepEqual(after.filters, {});
      assert(BookWriter.Domain.search(after.collection, after.query, after.filters)
        .indexOf(newId) !== -1);
      BookWriter.App._test.clearPendingSave();
      BookWriter.Persistence._setStorageForTests(null);
      BookWriter.App._test.resetState();
    });
  });

  test("app: erase cancels pending autosave and remains first-run after the delay", function () {
    var stub = stubUi({ confirm: true });
    var storage = fakeStorage();
    return withStubbedUi(stub, function () {
      BookWriter.Persistence._setStorageForTests(storage);
      BookWriter.App._test.resetState({
        collection: sampleCollection(),
        selectedMemoryId: IDS.memory1,
        firstRun: false
      });
      BookWriter.App._test.updateMemory(IDS.memory1, "title", "Edited before erase");
      BookWriter.App._test.eraseAll();
      return new Promise(function (resolve) {
        window.setTimeout(resolve, 400);
      }).then(function () {
        var after = BookWriter.App._test.getState();
        equal(storage.raw(BookWriter.Persistence.STORAGE_KEY), null);
        equal(after.firstRun, true);
        assert(BookWriter.App._test.isEmpty(after.collection));
        BookWriter.Persistence._setStorageForTests(null);
        BookWriter.App._test.resetState();
      });
    });
  });

  test("app: force overwrite treats a missing stored key as terminal erase before the null event arrives", function () {
    var stub = stubUi({ confirm: true });
    var storedCollection = sampleCollection();
    storedCollection.revision = 4;
    var localCollection = clone(storedCollection);
    localCollection.revision = 3;
    var storage = fakeStorage((function () {
      var data = {};
      data[BookWriter.Persistence.STORAGE_KEY] = JSON.stringify(storedCollection);
      return data;
    }()));
    return withStubbedUi(stub, function () {
      BookWriter.Persistence._setStorageForTests(storage);
      BookWriter.App._test.resetState({
        collection: clone(localCollection),
        selectedMemoryId: IDS.memory1,
        query: "amma",
        filters: {
          chapterId: IDS.chapter1,
          theme: "Family"
        },
        firstRun: false
      });
      BookWriter.App._test.updateMemory(IDS.memory1, "title", "Sensitive local draft");
      BookWriter.App._test.getState().conflicted = true;
      storage.removeItem(BookWriter.Persistence.STORAGE_KEY);
      BookWriter.App._test.forceOverwrite();
      return new Promise(function (resolve) {
        window.setTimeout(resolve, 400);
      });
    }).then(function () {
      var after = BookWriter.App._test.getState();
      equal(storage.raw(BookWriter.Persistence.STORAGE_KEY), null);
      equal(storage.setCalls, 0);
      equal(after.firstRun, true);
      equal(after.conflicted, false);
      equal(after.selectedMemoryId, null);
      equal(after.query, "");
      deepEqual(after.filters, {});
      assert(BookWriter.App._test.isEmpty(after.collection));
      equal(stub.log.transientClears, 1);
      BookWriter.Persistence._setStorageForTests(null);
      BookWriter.App._test.resetState();
    });
  });

  test("app: storage-key deletion clears in-memory data and blocks stale overwrite resurrection", function () {
    var stub = stubUi({ confirm: true });
    var collection = sampleCollection();
    collection.revision = 3;
    var data = {};
    data[BookWriter.Persistence.STORAGE_KEY] = JSON.stringify(collection);
    var storage = fakeStorage(data);
    return withStubbedUi(stub, function () {
      BookWriter.Persistence._setStorageForTests(storage);
      BookWriter.App._test.resetState({
        collection: clone(collection),
        selectedMemoryId: IDS.memory1,
        query: "amma",
        filters: {
          chapterId: IDS.chapter1,
          theme: "Family"
        },
        firstRun: false,
        conflicted: true
      });
      BookWriter.App._test.updateMemory(IDS.memory1, "title", "Pending local change");
      storage.removeItem(BookWriter.Persistence.STORAGE_KEY);
      BookWriter.App._test.onExternalChange({ newValue: null });
      return new Promise(function (resolve) {
        window.setTimeout(resolve, 400);
      }).then(function () {
        var after = BookWriter.App._test.getState();
        equal(after.firstRun, true);
        equal(after.selectedMemoryId, null);
        equal(after.query, "");
        deepEqual(after.filters, {});
        equal(after.conflicted, false);
        assert(BookWriter.App._test.isEmpty(after.collection));
        equal(stub.log.transientClears, 1);
        BookWriter.App._test.forceOverwrite();
        return new Promise(function (resolve) {
          window.setTimeout(resolve, 0);
        });
      }).then(function () {
        equal(storage.raw(BookWriter.Persistence.STORAGE_KEY), null);
        equal(storage.setCalls, 0);
        BookWriter.Persistence._setStorageForTests(null);
        BookWriter.App._test.resetState();
      });
    });
  });

  test("performance: 500 memories meet load, search, and save thresholds", function () {
    var collection = performanceCollection();
    var storedData = {};
    storedData[BookWriter.Persistence.STORAGE_KEY] = JSON.stringify(collection);
    BookWriter.Persistence._setStorageForTests(fakeStorage(storedData));
    var loadStart = performance.now();
    var loaded = BookWriter.Persistence.load();
    var loadDuration = performance.now() - loadStart;
    assert(loaded.ok);
    assert(loadDuration < 2000, "Validation/load took " + loadDuration.toFixed(1) + " ms.");

    var searchStart = performance.now();
    var matches = BookWriter.Domain.search(collection, "needle", {});
    var searchDuration = performance.now() - searchStart;
    equal(matches.length, 1);
    assert(searchDuration < 300, "Search took " + searchDuration.toFixed(1) + " ms.");

    var firstMemoryId = collection.chapters[0].memoryIds[0];
    var reorderStart = performance.now();
    var reordered = BookWriter.Domain.apply(collection, {
      type: "MOVE_MEMORY",
      memoryId: firstMemoryId,
      targetChapterId: collection.chapters[0].id,
      targetIndex: collection.chapters[0].memoryIds.length - 1
    }, TIME);
    var reorderDuration = performance.now() - reorderStart;
    assert(reordered.ok);
    assert(reorderDuration < 300, "Reorder took " + reorderDuration.toFixed(1) + " ms.");

    var storage = fakeStorage();
    BookWriter.Persistence._setStorageForTests(storage);
    var saveStart = performance.now();
    var saved = BookWriter.Persistence.save(collection, 0);
    var saveDuration = performance.now() - saveStart;
    assert(saved.ok);
    assert(saveDuration < 300, "Save took " + saveDuration.toFixed(1) + " ms.");

    var metrics = document.createElement("p");
    metrics.id = "performance-metrics";
    metrics.textContent = "NFR-8 timings — load " + loadDuration.toFixed(1) +
      " ms; search " + searchDuration.toFixed(1) + " ms; reorder " +
      reorderDuration.toFixed(1) + " ms; save " + saveDuration.toFixed(1) + " ms.";
    document.querySelector("main").appendChild(metrics);
  });

  function run() {
    var summary = document.getElementById("test-summary");
    var results = document.getElementById("test-results");
    var passed = 0;
    var failed = 0;
    var chain = Promise.resolve();

    tests.forEach(function (entry) {
      chain = chain.then(function () {
        return Promise.resolve().then(entry.callback).then(function () {
          passed += 1;
          results.appendChild((function () {
            var item = document.createElement("li");
            item.className = "pass";
            item.textContent = "PASS — " + entry.name;
            return item;
          }()));
        }).catch(function (error) {
          failed += 1;
          results.appendChild((function () {
            var item = document.createElement("li");
            item.className = "fail";
            item.textContent = "FAIL — " + entry.name + ": " + error.message;
            return item;
          }()));
        });
      });
    });

    chain.then(function () {
      BookWriter.Persistence._setStorageForTests(null);
      summary.textContent = passed + " passed, " + failed + " failed, " +
        tests.length + " total.";
      summary.setAttribute("data-status", failed === 0 ? "passed" : "failed");
      document.title = failed === 0 ?
        "PASS — Life Memoir Writer tests" : "FAIL — Life Memoir Writer tests";
    });
  }

  run();
}(window.BookWriter, document, window));
