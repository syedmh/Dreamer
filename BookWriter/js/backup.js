(function (BookWriter) {
  "use strict";

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

  function serialize(collection, appVersion, exportedAt) {
    var validated = BookWriter.Validation.validateCollection(collection);
    if (!validated.ok) {
      throw new TypeError("Cannot back up an invalid collection.");
    }
    if (typeof appVersion !== "string" || !appVersion ||
        typeof exportedAt !== "string" || !exportedAt) {
      throw new TypeError("Backup metadata is required.");
    }
    return JSON.stringify({
      format: "dreamer.life-memoir.backup",
      formatVersion: 1,
      exportedAt: exportedAt,
      appVersion: appVersion,
      collection: validated.value
    }, null, 2);
  }

  function readFileText(file) {
    if (typeof file.text === "function") {
      return file.text();
    }
    return new Promise(function (resolve, reject) {
      var reader = new FileReader();
      reader.addEventListener("load", function () {
        resolve(String(reader.result));
      });
      reader.addEventListener("error", function () {
        reject(reader.error);
      });
      reader.readAsText(file, "UTF-8");
    });
  }

  function inspect(file) {
    if (!file || typeof file.size !== "number") {
      return Promise.resolve(failure("WRONG_FORMAT", "Choose a JSON backup file."));
    }
    if (file.type && file.type !== "application/json") {
      return Promise.resolve(failure("WRONG_FORMAT",
        "The selected file must use the application/json file type."));
    }
    if (file.size > BookWriter.Validation.MAX_BACKUP_BYTES) {
      return Promise.resolve(failure("FILE_TOO_LARGE",
        "The backup exceeds the 10 MiB restore limit.", {
          maximumBytes: BookWriter.Validation.MAX_BACKUP_BYTES,
          actualBytes: file.size
        }));
    }

    return readFileText(file).then(function (text) {
      var candidate;
      try {
        candidate = JSON.parse(text);
      } catch (error) {
        return failure("INVALID_JSON", "The selected backup is not valid JSON.");
      }
      return BookWriter.Validation.validateBackup(candidate, file.size);
    }).catch(function () {
      return failure("INVALID_JSON", "The selected backup could not be read.");
    });
  }

  function commit(preview, expectedRevision) {
    if (!preview || !preview.collection ||
        !Number.isInteger(expectedRevision) || expectedRevision < 0) {
      return failure("INVALID_SCHEMA", "The restore preview or revision is invalid.");
    }
    var validated = BookWriter.Validation.validateCollection(preview.collection);
    if (!validated.ok) {
      return validated;
    }
    var collection = clone(validated.value);
    collection.revision = expectedRevision;
    return success(collection);
  }

  BookWriter.Backup = Object.freeze({
    FORMAT: "dreamer.life-memoir.backup",
    FORMAT_VERSION: 1,
    MIME_TYPE: "application/json",
    serialize: serialize,
    inspect: inspect,
    commit: commit
  });
}(window.BookWriter));
