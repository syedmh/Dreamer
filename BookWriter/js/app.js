(function (BookWriter, window, document) {
  "use strict";

  var APP_VERSION = "1.0.0";
  var AUTOSAVE_DELAY = 300;
  var state = {
    collection: null,
    activeView: "memories",
    selectedMemoryId: null,
    query: "",
    filters: {},
    firstRun: true,
    editingDisabled: false,
    conflicted: false,
    saveTimer: null,
    unsubscribe: null
  };

  function now() {
    return new Date().toISOString();
  }

  function createUuid() {
    if (window.crypto && typeof window.crypto.randomUUID === "function") {
      return window.crypto.randomUUID();
    }
    var bytes = new Uint8Array(16);
    if (window.crypto && typeof window.crypto.getRandomValues === "function") {
      window.crypto.getRandomValues(bytes);
    } else {
      for (var index = 0; index < bytes.length; index += 1) {
        bytes[index] = Math.floor(Math.random() * 256);
      }
    }
    bytes[6] = (bytes[6] & 15) | 64;
    bytes[8] = (bytes[8] & 63) | 128;
    var hex = Array.prototype.map.call(bytes, function (byte) {
      return byte.toString(16).padStart(2, "0");
    }).join("");
    return hex.slice(0, 8) + "-" + hex.slice(8, 12) + "-" + hex.slice(12, 16) +
      "-" + hex.slice(16, 20) + "-" + hex.slice(20);
  }

  function ids() {
    return {
      collectionId: createUuid,
      memoryId: createUuid,
      chapterId: createUuid
    };
  }

  function emptyCollection() {
    return BookWriter.Domain.createEmptyCollection(now(), ids());
  }

  function isEmpty(collection) {
    return Object.keys(collection.memories).length === 0 &&
      collection.chapters.length === 0 &&
      !collection.book.title &&
      !collection.book.subtitle &&
      !collection.book.authorName &&
      !collection.book.dedication &&
      !collection.book.preface;
  }

  function splitList(value) {
    var seen = {};
    return String(value || "").split(",").map(function (item) {
      return item.trim();
    }).filter(function (item) {
      var key = item.toLocaleLowerCase();
      if (!item || seen[key]) {
        return false;
      }
      seen[key] = true;
      return true;
    });
  }

  function allThemes(collection) {
    var byNormalizedTheme = {};
    Object.keys(collection.memories).forEach(function (memoryId) {
      collection.memories[memoryId].themes.forEach(function (theme) {
        var normalized = theme.toLocaleLowerCase();
        if (!byNormalizedTheme[normalized]) {
          byNormalizedTheme[normalized] = theme;
        }
      });
    });
    return Object.keys(byNormalizedTheme).map(function (key) {
      return byNormalizedTheme[key];
    }).sort(function (left, right) {
      return left.localeCompare(right);
    });
  }

  function firstOrderedMemoryId(collection) {
    var results = BookWriter.Domain.search(collection, "", {});
    return results.length ? results[0] : null;
  }

  function viewModel() {
    var memoryIds = BookWriter.Domain.search(state.collection, state.query, state.filters);
    if (state.selectedMemoryId && memoryIds.indexOf(state.selectedMemoryId) === -1) {
      state.selectedMemoryId = memoryIds.length ? memoryIds[0] : null;
    }
    if (!state.selectedMemoryId && memoryIds.length) {
      state.selectedMemoryId = memoryIds[0];
    }
    return {
      collection: state.collection,
      activeView: state.activeView,
      selectedMemoryId: state.selectedMemoryId,
      query: state.query,
      filters: state.filters,
      themes: allThemes(state.collection),
      memoryIds: memoryIds,
      firstRun: state.firstRun,
      editingDisabled: state.editingDisabled,
      conflicted: state.conflicted
    };
  }

  function render() {
    BookWriter.UI.render(viewModel());
  }

  function clearPendingSave() {
    if (state.saveTimer !== null) {
      window.clearTimeout(state.saveTimer);
      state.saveTimer = null;
    }
  }

  function enterFirstRunState(collection, status, detail) {
    state.collection = collection || emptyCollection();
    state.activeView = "memories";
    state.selectedMemoryId = null;
    state.query = "";
    state.filters = {};
    state.firstRun = true;
    state.editingDisabled = false;
    state.conflicted = false;
    BookWriter.UI.setStorageError(null);
    if (typeof BookWriter.UI.clearTransientState === "function") {
      BookWriter.UI.clearTransientState();
    }
    BookWriter.UI.setSaveStatus(status, detail);
    render();
  }

  function describeSaveError(error) {
    return error.message + " Your on-screen draft remains available. Download a backup before closing this page.";
  }

  function performSave() {
    state.saveTimer = null;
    if (state.editingDisabled || state.conflicted) {
      return;
    }
    BookWriter.UI.setSaveStatus("Saving…", "Writing one local snapshot.");
    var result = BookWriter.Persistence.save(state.collection, state.collection.revision);
    if (result.ok) {
      state.collection.revision = result.value.revision;
      state.firstRun = false;
      BookWriter.UI.setStorageError(null);
      BookWriter.UI.setSaveStatus("Saved locally",
        "Last saved " + new Date(result.value.savedAt).toLocaleString() +
        ". Download backups regularly.");
      return;
    }
    if (result.error.code === "STALE_REVISION") {
      state.conflicted = true;
      BookWriter.UI.setSaveStatus("Save paused", "A newer stored copy needs your decision.");
      render();
      return;
    }
    BookWriter.UI.setSaveStatus("Save failed", "Draft retained on screen.");
    BookWriter.UI.setStorageError({
      code: result.error.code,
      message: describeSaveError(result.error)
    });
  }

  function scheduleSave() {
    if (state.editingDisabled) {
      return;
    }
    clearPendingSave();
    BookWriter.UI.setSaveStatus("Unsaved changes", "Autosave will run shortly.");
    if (!state.conflicted) {
      state.saveTimer = window.setTimeout(performSave, AUTOSAVE_DELAY);
    }
  }

  function applyCommand(command, renderAfter) {
    var result = BookWriter.Domain.apply(state.collection, command, now());
    if (!result.ok) {
      BookWriter.UI.setSaveStatus("Change not applied",
        result.error.message + " (" + result.error.code + ")");
      return false;
    }
    state.collection = result.value;
    state.firstRun = isEmpty(state.collection);
    scheduleSave();
    if (renderAfter !== false) {
      render();
    }
    return true;
  }

  function createMemory() {
    var memoryId = createUuid();
    if (applyCommand({
      type: "CREATE_MEMORY",
      id: memoryId,
      fields: {}
    }, false)) {
      state.query = "";
      state.filters = {};
      state.selectedMemoryId = memoryId;
      state.activeView = "memories";
      state.firstRun = false;
      render();
    }
  }

  function updateMemory(memoryId, fieldName, value) {
    var fields = {};
    fields[fieldName] = ["people", "places", "themes"].indexOf(fieldName) === -1 ?
      value : splitList(value);
    applyCommand({
      type: "UPDATE_MEMORY",
      memoryId: memoryId,
      fields: fields
    }, false);
  }

  function deleteMemory(memoryId) {
    var memory = state.collection.memories[memoryId];
    if (!memory) {
      return;
    }
    BookWriter.UI.showConfirm({
      title: "Delete this memory?",
      message: "“" + (memory.title || "Untitled memory") +
        "” and its prompt responses and narrative will be removed. This cannot be undone.",
      acceptLabel: "Delete memory"
    }).then(function (confirmed) {
      if (!confirmed) {
        return;
      }
      if (applyCommand({
        type: "DELETE_MEMORY",
        memoryId: memoryId,
        confirmationToken: "DELETE_MEMORY"
      }, false)) {
        state.selectedMemoryId = firstOrderedMemoryId(state.collection);
        state.firstRun = isEmpty(state.collection);
        render();
      }
    });
  }

  function updatePromptResponse(memoryId, promptId, text) {
    applyCommand({
      type: "UPDATE_PROMPT_RESPONSE",
      memoryId: memoryId,
      promptId: promptId,
      text: text
    }, false);
  }

  function updateNarrative(memoryId, text) {
    applyCommand({
      type: "UPDATE_NARRATIVE",
      memoryId: memoryId,
      text: text
    }, false);
  }

  function composeNarrativeText(memory) {
    var parts = [];
    if (memory.memoryText.trim()) {
      parts.push(memory.memoryText);
    }
    BookWriter.Prompts.catalogue.forEach(function (prompt) {
      var response = memory.promptResponses[prompt.id];
      if (response.trim()) {
        parts.push(prompt.label + "\n" + response);
      }
    });
    return parts.join("\n\n");
  }

  function composeNarrative(memoryId) {
    var memory = state.collection.memories[memoryId];
    if (!memory) {
      return;
    }
    var draft = composeNarrativeText(memory);
    if (!draft) {
      BookWriter.UI.setSaveStatus("Nothing copied",
        "Add source memory text or prompt responses first.");
      return;
    }
    function applyDraft() {
      if (applyCommand({
        type: "UPDATE_NARRATIVE",
        memoryId: memoryId,
        text: draft
      }, false)) {
        render();
      }
    }
    if (!memory.narrativeText) {
      applyDraft();
      return;
    }
    BookWriter.UI.showConfirm({
      title: "Replace the current narrative?",
      message: "This explicit action will replace the narrative editor with the source memory and non-empty prompt responses. The source memory remains unchanged.",
      acceptLabel: "Replace narrative"
    }).then(function (confirmed) {
      if (confirmed) {
        applyDraft();
      }
    });
  }

  function createChapter(title) {
    applyCommand({
      type: "CREATE_CHAPTER",
      id: createUuid(),
      title: title
    }, true);
  }

  function renameChapter(chapterId, title) {
    applyCommand({
      type: "RENAME_CHAPTER",
      chapterId: chapterId,
      title: title
    }, true);
  }

  function moveChapter(chapterId, direction) {
    applyCommand({
      type: "MOVE_CHAPTER",
      chapterId: chapterId,
      direction: direction
    }, true);
  }

  function deleteChapter(chapterId) {
    var chapter = state.collection.chapters.find(function (candidate) {
      return candidate.id === chapterId;
    });
    if (!chapter) {
      return;
    }
    BookWriter.UI.showConfirm({
      title: "Delete this chapter?",
      message: "The chapter “" + chapter.title + "” will be removed. Its " +
        chapter.memoryIds.length + " memories will remain and become unassigned.",
      acceptLabel: "Delete chapter"
    }).then(function (confirmed) {
      if (confirmed) {
        applyCommand({
          type: "DELETE_CHAPTER",
          chapterId: chapterId,
          confirmationToken: "DELETE_CHAPTER"
        }, true);
      }
    });
  }

  function destinationLength(chapterId) {
    if (chapterId === null) {
      return state.collection.unassignedMemoryIds.length;
    }
    var chapter = state.collection.chapters.find(function (candidate) {
      return candidate.id === chapterId;
    });
    return chapter ? chapter.memoryIds.length : 0;
  }

  function assignMemory(memoryId, chapterId) {
    applyCommand({
      type: "MOVE_MEMORY",
      memoryId: memoryId,
      targetChapterId: chapterId,
      targetIndex: destinationLength(chapterId)
    }, true);
  }

  function moveMemory(memoryId, chapterId, targetIndex) {
    applyCommand({
      type: "MOVE_MEMORY",
      memoryId: memoryId,
      targetChapterId: chapterId,
      targetIndex: targetIndex
    }, true);
  }

  function updateBook(fieldName, value) {
    var fields = {};
    fields[fieldName] = value;
    if (applyCommand({ type: "UPDATE_BOOK", fields: fields }, false)) {
      BookWriter.UI.renderBookPreview(state.collection);
    }
  }

  function download(content, mimeType, filename) {
    try {
      var blob = new Blob([content], { type: mimeType + ";charset=utf-8" });
      var objectUrl = window.URL.createObjectURL(blob);
      var anchor = document.createElement("a");
      anchor.href = objectUrl;
      anchor.download = filename;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      window.setTimeout(function () {
        window.URL.revokeObjectURL(objectUrl);
      }, 0);
      return { ok: true, value: undefined };
    } catch (error) {
      return {
        ok: false,
        error: {
          code: "DOWNLOAD_FAILED",
          message: "The browser could not create the download."
        }
      };
    }
  }

  function dateStamp() {
    return now().slice(0, 10);
  }

  function downloadBackup() {
    var serialized;
    try {
      serialized = BookWriter.Backup.serialize(state.collection, APP_VERSION, now());
    } catch (error) {
      BookWriter.UI.setSaveStatus("Backup failed", "The current draft could not be serialized.");
      return;
    }
    var result = download(serialized, "application/json",
      "life-memoir-backup-" + dateStamp() + ".json");
    if (!result.ok) {
      BookWriter.UI.setSaveStatus("Backup failed",
        result.error.message + " (" + result.error.code + ")");
    }
  }

  function restoreFile(file) {
    BookWriter.UI.setRestoreStatus("Reading and validating the backup…", false);
    return BookWriter.Backup.inspect(file).then(function (inspection) {
      if (!inspection.ok) {
        BookWriter.UI.setRestoreStatus(
          inspection.error.message + " (" + inspection.error.code + ")", true);
        return;
      }
      return BookWriter.UI.showRestorePreview(inspection.value).then(function (confirmed) {
        if (!confirmed) {
          BookWriter.UI.setRestoreStatus("Restore canceled. Current data was not changed.", false);
          return;
        }
        var prepared = BookWriter.Backup.commit(inspection.value, state.collection.revision);
        if (!prepared.ok) {
          BookWriter.UI.setRestoreStatus(
            prepared.error.message + " (" + prepared.error.code + ")", true);
          return;
        }
        BookWriter.UI.setSaveStatus("Restoring…", "Saving the replacement snapshot.");
        var saveResult = BookWriter.Persistence.save(
          prepared.value, state.collection.revision);
        if (!saveResult.ok) {
          if (saveResult.error.code === "STALE_REVISION") {
            state.conflicted = true;
            render();
          }
          BookWriter.UI.setRestoreStatus(
            saveResult.error.message + " (" + saveResult.error.code +
            "). Current data was not replaced.", true);
          return;
        }
        prepared.value.revision = saveResult.value.revision;
        state.collection = prepared.value;
        state.selectedMemoryId = firstOrderedMemoryId(state.collection);
        state.query = "";
        state.filters = {};
        state.firstRun = isEmpty(state.collection);
        state.conflicted = false;
        BookWriter.UI.setStorageError(null);
        BookWriter.UI.setSaveStatus("Restored and saved locally",
          "Replacement saved " + new Date(saveResult.value.savedAt).toLocaleString() + ".");
        BookWriter.UI.setRestoreStatus("Restore completed successfully.", false);
        render();
      });
    });
  }

  function exportHtml() {
    var result = download(BookWriter.Export.toHtml(state.collection), "text/html",
      "life-memoir-" + dateStamp() + ".html");
    if (!result.ok) {
      BookWriter.UI.setSaveStatus("Export failed",
        result.error.message + " (" + result.error.code + ")");
    }
  }

  function exportText() {
    var result = download(BookWriter.Export.toText(state.collection), "text/plain",
      "life-memoir-" + dateStamp() + ".txt");
    if (!result.ok) {
      BookWriter.UI.setSaveStatus("Export failed",
        result.error.message + " (" + result.error.code + ")");
    }
  }

  function eraseAll() {
    BookWriter.UI.showConfirm({
      title: "Erase all local memoir data?",
      message: "This removes the working memoir from this browser only. Downloaded backups and exports remain. This cannot be undone.",
      acceptLabel: "Erase all local data"
    }).then(function (confirmed) {
      if (!confirmed) {
        return;
      }
      var eraseResult = BookWriter.Persistence.erase(state.collection.revision);
      if (!eraseResult.ok) {
        BookWriter.UI.setStorageError({
          code: eraseResult.error.code,
          message: eraseResult.error.message
        });
        return;
      }
      clearPendingSave();
      var cleared = BookWriter.Domain.apply(state.collection, {
        type: "ERASE_COLLECTION",
        confirmationToken: "ERASE_ALL",
        newCollectionId: createUuid()
      }, now());
      if (!cleared.ok) {
        BookWriter.UI.setSaveStatus("Erase incomplete", cleared.error.message);
        return;
      }
      enterFirstRunState(cleared.value, "Local data erased",
        "No memoir is stored. Create a memory or restore a backup.");
    });
  }

  function reloadExternal() {
    var result = BookWriter.Persistence.load();
    if (!result.ok) {
      BookWriter.UI.setStorageError(result.error);
      return;
    }
    state.collection = result.value || emptyCollection();
    state.selectedMemoryId = firstOrderedMemoryId(state.collection);
    state.firstRun = isEmpty(state.collection);
    state.conflicted = false;
    BookWriter.UI.setSaveStatus(result.value ? "Stored copy loaded" : "No stored memoir",
      result.value ? "This tab now uses the latest stored copy." :
        "Create a memory or restore a backup.");
    render();
  }

  function retrySave() {
    if (!state.editingDisabled) {
      performSave();
      return;
    }
    var loaded = BookWriter.Persistence.load();
    if (!loaded.ok) {
      BookWriter.UI.setStorageError(loaded.error);
      return;
    }
    state.editingDisabled = false;
    if (loaded.value) {
      state.collection = loaded.value;
      state.selectedMemoryId = firstOrderedMemoryId(state.collection);
      state.firstRun = isEmpty(state.collection);
      BookWriter.UI.setStorageError(null);
      BookWriter.UI.setSaveStatus("Storage restored",
        "The browser’s stored memoir is available again.");
      render();
      return;
    }
    var saved = BookWriter.Persistence.save(state.collection, state.collection.revision);
    if (!saved.ok) {
      state.editingDisabled = true;
      BookWriter.UI.setStorageError(saved.error);
      return;
    }
    state.collection.revision = saved.value.revision;
    BookWriter.UI.setStorageError(null);
    BookWriter.UI.setSaveStatus("Storage restored",
      "Local saving is available. You can begin writing.");
    render();
  }

  function forceOverwrite() {
    if (!state.conflicted) {
      return;
    }
    BookWriter.UI.showConfirm({
      title: "Overwrite the newer stored copy?",
      message: "This keeps this tab’s draft and discards the newer stored copy. No automatic merge is available.",
      acceptLabel: "Overwrite stored copy"
    }).then(function (confirmed) {
      if (!confirmed || !state.conflicted) {
        return;
      }
      var stored = BookWriter.Persistence.load();
      if (!stored.ok) {
        BookWriter.UI.setStorageError(stored.error);
        return;
      }
      if (stored.value === null) {
        clearPendingSave();
        enterFirstRunState(emptyCollection(), "Local data erased elsewhere",
          "Another tab erased the stored memoir. Create a memory or restore a backup.");
        return;
      }
      var storedRevision = stored.value.revision;
      var prepared = BookWriter.Backup.commit({
        collection: state.collection
      }, storedRevision);
      if (!prepared.ok) {
        BookWriter.UI.setStorageError(prepared.error);
        return;
      }
      var saved = BookWriter.Persistence.save(prepared.value, storedRevision);
      if (!saved.ok) {
        BookWriter.UI.setStorageError(saved.error);
        return;
      }
      prepared.value.revision = saved.value.revision;
      state.collection = prepared.value;
      state.conflicted = false;
      BookWriter.UI.setSaveStatus("Overwrite saved",
        "This tab is now the stored copy.");
      render();
    });
  }

  function onExternalChange(event) {
    if (event.newValue === null) {
      clearPendingSave();
      enterFirstRunState(emptyCollection(), "Local data erased elsewhere",
        "Another tab erased the stored memoir. Create a memory or restore a backup.");
      return;
    }
    if (event.newValue !== null) {
      try {
        var candidate = JSON.parse(event.newValue);
        if (candidate.revision === state.collection.revision) {
          return;
        }
      } catch (error) {
        // Invalid external storage still requires an explicit user decision.
      }
    }
    clearPendingSave();
    state.conflicted = true;
    BookWriter.UI.setSaveStatus("Save paused",
      "Another tab changed the stored memoir.");
    render();
  }

  function changeChapterFilter(value) {
    if (value === "") {
      delete state.filters.chapterId;
    } else {
      state.filters.chapterId = value === "__unassigned__" ? null : value;
    }
    render();
  }

  function initUi() {
    BookWriter.UI.init({
      changeView: function (viewName) {
        state.activeView = viewName;
        render();
      },
      createMemory: createMemory,
      selectMemory: function (memoryId) {
        state.selectedMemoryId = memoryId;
        render();
      },
      changeSearch: function (query) {
        state.query = query;
        render();
      },
      changeChapterFilter: changeChapterFilter,
      changeThemeFilter: function (theme) {
        if (theme) {
          state.filters.theme = theme;
        } else {
          delete state.filters.theme;
        }
        render();
      },
      updateMemory: updateMemory,
      deleteMemory: deleteMemory,
      updatePromptResponse: updatePromptResponse,
      updateNarrative: updateNarrative,
      composeNarrative: composeNarrative,
      createChapter: createChapter,
      renameChapter: renameChapter,
      moveChapter: moveChapter,
      deleteChapter: deleteChapter,
      assignMemory: assignMemory,
      moveMemory: moveMemory,
      updateBook: updateBook,
      downloadBackup: downloadBackup,
      restoreFile: restoreFile,
      exportHtml: exportHtml,
      exportText: exportText,
      eraseAll: eraseAll,
      retrySave: retrySave,
      reloadExternal: reloadExternal,
      forceOverwrite: forceOverwrite
    });
  }

  function boot() {
    if (!document.getElementById("book-writer-app")) {
      return;
    }
    initUi();
    var loaded = BookWriter.Persistence.load();
    if (!loaded.ok) {
      state.collection = emptyCollection();
      state.editingDisabled = true;
      state.firstRun = true;
      BookWriter.UI.setSaveStatus("Storage unavailable",
        "Editing is paused because this browser cannot provide durable local storage.");
      BookWriter.UI.setStorageError(loaded.error);
    } else {
      state.collection = loaded.value || emptyCollection();
      state.selectedMemoryId = firstOrderedMemoryId(state.collection);
      state.firstRun = isEmpty(state.collection);
      BookWriter.UI.setSaveStatus(loaded.value ? "Saved locally" : "Not saved yet",
        loaded.value ? "Loaded the browser’s local memoir copy." :
          "Create a memory or restore a backup. Download backups regularly.");
    }
    state.unsubscribe = BookWriter.Persistence.subscribeExternalChange(onExternalChange);
    render();
  }

  function resetStateForTests(overrides) {
    clearPendingSave();
    if (state.unsubscribe) {
      state.unsubscribe();
      state.unsubscribe = null;
    }
    state.collection = emptyCollection();
    state.activeView = "memories";
    state.selectedMemoryId = null;
    state.query = "";
    state.filters = {};
    state.firstRun = true;
    state.editingDisabled = false;
    state.conflicted = false;
    if (overrides && typeof overrides === "object") {
      Object.keys(overrides).forEach(function (key) {
        state[key] = overrides[key];
      });
    }
    if (!state.collection) {
      state.collection = emptyCollection();
    }
    if (!overrides || !Object.prototype.hasOwnProperty.call(overrides, "firstRun")) {
      state.firstRun = isEmpty(state.collection);
    }
  }

  BookWriter.App = Object.freeze({
    boot: boot,
    _test: Object.freeze({
      splitList: splitList,
      isEmpty: isEmpty,
      createUuid: createUuid,
      composeNarrativeText: composeNarrativeText,
      clearPendingSave: clearPendingSave,
      createMemory: createMemory,
      updateMemory: updateMemory,
      restoreFile: restoreFile,
      eraseAll: eraseAll,
      forceOverwrite: forceOverwrite,
      onExternalChange: onExternalChange,
      viewModel: viewModel,
      getState: function () {
        return state;
      },
      resetState: resetStateForTests
    })
  });

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
}(window.BookWriter, window, document));
