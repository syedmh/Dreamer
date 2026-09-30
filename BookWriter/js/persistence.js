(function (BookWriter, window) {
  "use strict";

  var STORAGE_KEY = "dreamer.bookWriter.collection.v1";
  var storageOverride = null;

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

  function getStorage() {
    return storageOverride || window.localStorage;
  }

  function storageFailure(error) {
    var quota = error && (
      error.name === "QuotaExceededError" ||
      error.name === "NS_ERROR_DOM_QUOTA_REACHED" ||
      error.code === 22 ||
      error.code === 1014
    );
    if (quota) {
      return failure("STORAGE_QUOTA",
        "The browser storage limit was reached. Your on-screen draft is still available; download an emergency backup.");
    }
    return failure("STORAGE_UNAVAILABLE",
      "Local browser storage is unavailable. Your on-screen draft is not safely stored; download a backup if possible.");
  }

  function parseStored(raw) {
    var candidate;
    try {
      candidate = JSON.parse(raw);
    } catch (error) {
      return failure("INVALID_JSON", "The stored memoir is not valid JSON.");
    }
    return BookWriter.Validation.validateCollection(candidate);
  }

  function load() {
    var raw;
    try {
      raw = getStorage().getItem(STORAGE_KEY);
    } catch (error) {
      return storageFailure(error);
    }
    if (raw === null) {
      return success(null);
    }
    return parseStored(raw);
  }

  function readStoredRevision(storage) {
    var raw = storage.getItem(STORAGE_KEY);
    if (raw === null) {
      return success(0);
    }
    var parsed = parseStored(raw);
    if (!parsed.ok) {
      return parsed;
    }
    return success(parsed.value.revision);
  }

  function save(collection, expectedRevision) {
    if (!Number.isInteger(expectedRevision) || expectedRevision < 0) {
      return failure("INVALID_SCHEMA", "The expected revision is invalid.");
    }
    var validated = BookWriter.Validation.validateCollection(collection);
    if (!validated.ok) {
      return validated;
    }
    if (validated.value.revision !== expectedRevision) {
      return failure("STALE_REVISION",
        "This draft revision does not match the revision it expects to replace.", {
          expectedRevision: expectedRevision,
          draftRevision: validated.value.revision
        });
    }

    var storage;
    var storedRevision;
    try {
      storage = getStorage();
      storedRevision = readStoredRevision(storage);
    } catch (error) {
      return storageFailure(error);
    }
    if (!storedRevision.ok) {
      return storedRevision;
    }
    if (storedRevision.value !== expectedRevision) {
      return failure("STALE_REVISION",
        "A newer memoir copy is stored in another tab. Reload it or explicitly overwrite it.", {
          expectedRevision: expectedRevision,
          storedRevision: storedRevision.value
        });
    }

    var snapshot = validated.value;
    var savedAt = new Date().toISOString();
    snapshot.revision = expectedRevision + 1;
    try {
      storage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
    } catch (error) {
      return storageFailure(error);
    }
    return success({
      revision: snapshot.revision,
      savedAt: savedAt
    });
  }

  function erase(expectedRevision) {
    if (!Number.isInteger(expectedRevision) || expectedRevision < 0) {
      return failure("INVALID_SCHEMA", "The expected revision is invalid.");
    }
    var storage;
    var storedRevision;
    try {
      storage = getStorage();
      storedRevision = readStoredRevision(storage);
    } catch (error) {
      return storageFailure(error);
    }
    if (!storedRevision.ok) {
      return storedRevision;
    }
    if (storedRevision.value !== expectedRevision) {
      return failure("STALE_REVISION",
        "A newer memoir copy is stored in another tab. Reload before erasing.", {
          expectedRevision: expectedRevision,
          storedRevision: storedRevision.value
        });
    }
    try {
      storage.removeItem(STORAGE_KEY);
      if (storage.getItem(STORAGE_KEY) !== null) {
        return failure("STORAGE_UNAVAILABLE", "The browser did not remove the memoir data.");
      }
    } catch (error) {
      return storageFailure(error);
    }
    return success(undefined);
  }

  function subscribeExternalChange(callback) {
    if (typeof callback !== "function") {
      throw new TypeError("A storage change callback is required.");
    }
    function listener(event) {
      if (event.key === STORAGE_KEY) {
        callback(event);
      }
    }
    window.addEventListener("storage", listener);
    return function () {
      window.removeEventListener("storage", listener);
    };
  }

  function setStorageForTests(storage) {
    storageOverride = storage || null;
  }

  BookWriter.Persistence = Object.freeze({
    STORAGE_KEY: STORAGE_KEY,
    load: load,
    save: save,
    erase: erase,
    subscribeExternalChange: subscribeExternalChange,
    _setStorageForTests: setStorageForTests
  });
}(window.BookWriter, window));
