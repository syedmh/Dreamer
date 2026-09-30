(function (BookWriter, document) {
  "use strict";

  var handlers = {};
  var confirmResolver = null;
  var confirmReturnFocus = null;
  var restoreResolver = null;
  var restoreReturnFocus = null;

  function byId(id) {
    return document.getElementById(id);
  }

  function element(tagName, className, text) {
    var node = document.createElement(tagName);
    if (className) {
      node.className = className;
    }
    if (text !== undefined) {
      node.textContent = text;
    }
    return node;
  }

  function option(value, label) {
    var node = element("option", "", label);
    node.value = value;
    return node;
  }

  function labelWithControl(labelText, control, className) {
    var label = element("label", className || "");
    label.appendChild(document.createTextNode(labelText));
    label.appendChild(control);
    return label;
  }

  function inputControl(type, value, name) {
    var input = document.createElement("input");
    input.type = type;
    input.value = value || "";
    if (name) {
      input.name = name;
    }
    return input;
  }

  function textareaControl(value, rows, name) {
    var textarea = document.createElement("textarea");
    textarea.value = value || "";
    textarea.rows = rows;
    if (name) {
      textarea.name = name;
    }
    return textarea;
  }

  function callHandler(name) {
    var args = Array.prototype.slice.call(arguments, 1);
    if (typeof handlers[name] === "function") {
      handlers[name].apply(null, args);
    }
  }

  function showView(viewName) {
    ["memories", "chapters", "book", "data"].forEach(function (name) {
      byId("view-" + name).hidden = name !== viewName;
    });
    document.querySelectorAll(".app-nav button[data-view]").forEach(function (button) {
      if (button.getAttribute("data-view") === viewName) {
        button.setAttribute("aria-current", "page");
      } else {
        button.removeAttribute("aria-current");
      }
    });
  }

  function setSaveStatus(status, detail) {
    byId("save-status").textContent = status;
    byId("save-detail").textContent = detail || "";
  }

  function setStorageError(error) {
    var banner = byId("storage-banner");
    if (!error) {
      banner.hidden = true;
      return;
    }
    byId("storage-message").textContent = error.message + " (" + error.code + ")";
    banner.hidden = false;
    banner.focus();
  }

  function setConflict(visible) {
    var banner = byId("conflict-banner");
    banner.hidden = !visible;
    if (visible) {
      banner.focus();
    }
  }

  function setRestoreStatus(message, isError) {
    var status = byId("restore-status");
    status.textContent = message || "";
    status.className = isError ? "banner-error" : "muted";
  }

  function finishConfirm(value) {
    if (!confirmResolver) {
      return;
    }
    var resolve = confirmResolver;
    var focusTarget = confirmReturnFocus;
    confirmResolver = null;
    confirmReturnFocus = null;
    if (byId("confirm-dialog").open) {
      byId("confirm-dialog").close();
    }
    resolve(value);
    if (focusTarget && typeof focusTarget.focus === "function") {
      focusTarget.focus();
    }
  }

  function showConfirm(options) {
    if (confirmResolver) {
      finishConfirm(false);
    }
    confirmReturnFocus = document.activeElement;
    byId("confirm-title").textContent = options.title;
    byId("confirm-message").textContent = options.message;
    byId("confirm-accept").textContent = options.acceptLabel || "Confirm";
    byId("confirm-dialog").showModal();
    byId("confirm-cancel").focus();
    return new Promise(function (resolve) {
      confirmResolver = resolve;
    });
  }

  function finishRestore(value) {
    if (!restoreResolver) {
      return;
    }
    var resolve = restoreResolver;
    var focusTarget = restoreReturnFocus;
    restoreResolver = null;
    restoreReturnFocus = null;
    if (byId("restore-dialog").open) {
      byId("restore-dialog").close();
    }
    resolve(value);
    if (focusTarget && typeof focusTarget.focus === "function") {
      focusTarget.focus();
    }
  }

  function clearTransientState() {
    finishConfirm(false);
    finishRestore(false);
    setConflict(false);
    setRestoreStatus("", false);
  }

  function showRestorePreview(preview) {
    if (restoreResolver) {
      finishRestore(false);
    }
    restoreReturnFocus = document.activeElement;
    byId("restore-summary").textContent =
      "Backup from " + preview.exportedAt + ": " + preview.memoryCount +
      " memories and " + preview.chapterCount + " chapters.";
    byId("restore-dialog").showModal();
    byId("restore-cancel").focus();
    return new Promise(function (resolve) {
      restoreResolver = resolve;
    });
  }

  function renderFilters(viewModel) {
    var chapterFilter = byId("chapter-filter");
    var currentChapter = viewModel.filters.chapterId;
    chapterFilter.replaceChildren(
      option("", "All chapters"),
      option("__unassigned__", "Unassigned")
    );
    viewModel.collection.chapters.forEach(function (chapter) {
      chapterFilter.appendChild(option(chapter.id, chapter.title));
    });
    chapterFilter.value = currentChapter === undefined ? "" :
      (currentChapter === null ? "__unassigned__" : currentChapter);

    var themeFilter = byId("theme-filter");
    themeFilter.replaceChildren(option("", "All themes"));
    viewModel.themes.forEach(function (theme) {
      themeFilter.appendChild(option(theme, theme));
    });
    themeFilter.value = viewModel.filters.theme || "";
    byId("memory-search").value = viewModel.query;
  }

  function renderMemoryList(viewModel) {
    var list = byId("memory-list");
    list.replaceChildren();
    byId("memory-count").textContent = viewModel.memoryIds.length + " of " +
      Object.keys(viewModel.collection.memories).length + " memories";

    if (!viewModel.memoryIds.length) {
      list.appendChild(element("p", "empty-state", "No memories match these filters."));
      return;
    }

    viewModel.memoryIds.forEach(function (memoryId) {
      var memory = viewModel.collection.memories[memoryId];
      var button = element("button", "", memory.title || "Untitled memory");
      button.type = "button";
      button.setAttribute("aria-current",
        viewModel.selectedMemoryId === memoryId ? "true" : "false");
      var detail = memory.dateText || memory.themes.join(", ") || "No date or theme yet";
      button.appendChild(element("small", "", detail));
      button.addEventListener("click", function () {
        callHandler("selectMemory", memoryId);
      });
      list.appendChild(button);
    });
  }

  function attachMemoryField(control, memoryId, fieldName, transform, disabled) {
    control.disabled = disabled;
    control.addEventListener("input", function () {
      var value = transform ? transform(control.value) : control.value;
      callHandler("updateMemory", memoryId, fieldName, value);
    });
    return control;
  }

  function renderMemoryEditor(viewModel) {
    var container = byId("memory-editor");
    container.replaceChildren();
    var memory = viewModel.collection.memories[viewModel.selectedMemoryId];
    if (!memory) {
      container.appendChild(element("p", "empty-state",
        "Create or choose a memory to begin."));
      return;
    }

    var header = element("div", "editor-header");
    var heading = element("h3", "", memory.title || "Untitled memory");
    var deleteButton = element("button", "danger", "Delete memory");
    deleteButton.type = "button";
    deleteButton.disabled = viewModel.editingDisabled;
    deleteButton.addEventListener("click", function () {
      callHandler("deleteMemory", memory.id);
    });
    header.appendChild(heading);
    header.appendChild(deleteButton);
    container.appendChild(header);

    var form = element("div", "form-grid");
    var title = attachMemoryField(inputControl("text", memory.title), memory.id,
      "title", null, viewModel.editingDisabled);
    var dateText = attachMemoryField(inputControl("text", memory.dateText), memory.id,
      "dateText", null, viewModel.editingDisabled);
    var memoryText = attachMemoryField(textareaControl(memory.memoryText, 10), memory.id,
      "memoryText", null, viewModel.editingDisabled);
    var people = attachMemoryField(inputControl("text", memory.people.join(", ")), memory.id,
      "people", null, viewModel.editingDisabled);
    var places = attachMemoryField(inputControl("text", memory.places.join(", ")), memory.id,
      "places", null, viewModel.editingDisabled);
    var themes = attachMemoryField(inputControl("text", memory.themes.join(", ")), memory.id,
      "themes", null, viewModel.editingDisabled);
    var senses = attachMemoryField(textareaControl(memory.sensoryDetails, 5), memory.id,
      "sensoryDetails", null, viewModel.editingDisabled);

    form.appendChild(labelWithControl("Title", title));
    form.appendChild(labelWithControl("Date or date description", dateText));
    form.appendChild(labelWithControl("What do you remember?", memoryText, "span-two"));
    form.appendChild(labelWithControl("People (comma separated)", people));
    form.appendChild(labelWithControl("Places (comma separated)", places));
    form.appendChild(labelWithControl("Themes (comma separated)", themes));
    form.appendChild(labelWithControl("Sensory details", senses, "span-two"));
    container.appendChild(form);

    var promptHeading = element("h3", "", "Optional prompts");
    promptHeading.style.marginTop = "2rem";
    container.appendChild(promptHeading);
    container.appendChild(element("p", "help-text",
      "Skip any prompt. Responses never overwrite your source memory or narrative."));
    var promptList = element("div", "prompt-list");
    BookWriter.Prompts.catalogue.forEach(function (prompt) {
      var card = element("section", "prompt-card");
      card.appendChild(element("h3", "", prompt.label));
      card.appendChild(element("p", "", prompt.text));
      var response = textareaControl(memory.promptResponses[prompt.id], 4);
      response.setAttribute("aria-label", prompt.label + " response");
      response.disabled = viewModel.editingDisabled;
      response.addEventListener("input", function () {
        callHandler("updatePromptResponse", memory.id, prompt.id, response.value);
      });
      card.appendChild(response);
      promptList.appendChild(card);
    });
    container.appendChild(promptList);

    var narrative = textareaControl(memory.narrativeText, 12);
    narrative.disabled = viewModel.editingDisabled;
    narrative.addEventListener("input", function () {
      callHandler("updateNarrative", memory.id, narrative.value);
    });
    var narrativeActions = element("div", "button-row");
    var composeButton = element("button", "secondary",
      "Create draft from memory and prompt responses");
    composeButton.type = "button";
    composeButton.disabled = viewModel.editingDisabled;
    composeButton.addEventListener("click", function () {
      callHandler("composeNarrative", memory.id);
    });
    narrativeActions.appendChild(composeButton);
    container.appendChild(narrativeActions);
    container.appendChild(labelWithControl(
      "Narrative prose (editable; leave blank to use the source memory in book view)",
      narrative, "form-stack"));
  }

  function chapterOptions(collection, selectedId) {
    var select = document.createElement("select");
    select.appendChild(option("__unassigned__", "Unassigned"));
    collection.chapters.forEach(function (chapter) {
      select.appendChild(option(chapter.id, chapter.title));
    });
    select.value = selectedId === null ? "__unassigned__" : selectedId;
    return select;
  }

  function memoryRow(viewModel, memoryId, chapterId, index, count) {
    var memory = viewModel.collection.memories[memoryId];
    var row = element("div", "ordered-memory");
    var description = element("p", "");
    description.appendChild(element("strong", "", memory.title || "Untitled memory"));
    if (memory.dateText) {
      description.appendChild(element("small", "", " — " + memory.dateText));
    }
    row.appendChild(description);

    var assignment = chapterOptions(viewModel.collection, chapterId);
    assignment.setAttribute("aria-label",
      "Chapter assignment for " + (memory.title || "untitled memory"));
    assignment.disabled = viewModel.editingDisabled;
    assignment.addEventListener("change", function () {
      callHandler("assignMemory", memoryId,
        assignment.value === "__unassigned__" ? null : assignment.value);
    });
    row.appendChild(assignment);

    var controls = element("div", "button-row");
    var up = element("button", "secondary small-button", "Move up");
    var down = element("button", "secondary small-button", "Move down");
    up.type = "button";
    down.type = "button";
    up.disabled = viewModel.editingDisabled || index === 0;
    down.disabled = viewModel.editingDisabled || index === count - 1;
    up.addEventListener("click", function () {
      callHandler("moveMemory", memoryId, chapterId, index - 1);
    });
    down.addEventListener("click", function () {
      callHandler("moveMemory", memoryId, chapterId, index + 1);
    });
    controls.appendChild(up);
    controls.appendChild(down);
    row.appendChild(controls);
    return row;
  }

  function renderChapterCard(viewModel, chapter, chapterIndex) {
    var card = element("section", "card chapter-card");
    var header = element("div", "chapter-card-header");
    var titleInput = inputControl("text", chapter.title);
    titleInput.setAttribute("aria-label", "Chapter title");
    titleInput.disabled = viewModel.editingDisabled;
    header.appendChild(labelWithControl("Chapter " + (chapterIndex + 1), titleInput));

    var actions = element("div", "button-row");
    var rename = element("button", "secondary small-button", "Save title");
    var up = element("button", "secondary small-button", "Move chapter up");
    var down = element("button", "secondary small-button", "Move chapter down");
    var remove = element("button", "danger small-button", "Delete chapter");
    [rename, up, down, remove].forEach(function (button) {
      button.type = "button";
      button.disabled = viewModel.editingDisabled;
    });
    up.disabled = viewModel.editingDisabled || chapterIndex === 0;
    down.disabled = viewModel.editingDisabled ||
      chapterIndex === viewModel.collection.chapters.length - 1;
    rename.addEventListener("click", function () {
      callHandler("renameChapter", chapter.id, titleInput.value);
    });
    up.addEventListener("click", function () {
      callHandler("moveChapter", chapter.id, "up");
    });
    down.addEventListener("click", function () {
      callHandler("moveChapter", chapter.id, "down");
    });
    remove.addEventListener("click", function () {
      callHandler("deleteChapter", chapter.id);
    });
    actions.appendChild(rename);
    actions.appendChild(up);
    actions.appendChild(down);
    actions.appendChild(remove);
    header.appendChild(actions);
    card.appendChild(header);

    var list = element("div", "ordered-memory-list");
    if (!chapter.memoryIds.length) {
      list.appendChild(element("p", "empty-state", "No memories in this chapter."));
    } else {
      chapter.memoryIds.forEach(function (memoryId, index) {
        list.appendChild(memoryRow(viewModel, memoryId, chapter.id, index,
          chapter.memoryIds.length));
      });
    }
    card.appendChild(list);
    return card;
  }

  function renderChapters(viewModel) {
    var organizer = byId("chapter-organizer");
    organizer.replaceChildren();
    viewModel.collection.chapters.forEach(function (chapter, index) {
      organizer.appendChild(renderChapterCard(viewModel, chapter, index));
    });

    var unassigned = element("section", "card chapter-card");
    unassigned.appendChild(element("h3", "", "Unassigned memories"));
    var list = element("div", "ordered-memory-list");
    if (!viewModel.collection.unassignedMemoryIds.length) {
      list.appendChild(element("p", "empty-state", "No unassigned memories."));
    } else {
      viewModel.collection.unassignedMemoryIds.forEach(function (memoryId, index) {
        list.appendChild(memoryRow(viewModel, memoryId, null, index,
          viewModel.collection.unassignedMemoryIds.length));
      });
    }
    unassigned.appendChild(list);
    organizer.appendChild(unassigned);
    byId("new-chapter-title").disabled = viewModel.editingDisabled;
  }

  function renderBookSections(container, sections) {
    container.replaceChildren();
    sections.forEach(function (section, index) {
      var sectionNode = element("section", "book-section");
      sectionNode.appendChild(element(index === 0 ? "h2" : "h3", "", section.title));
      if (!section.paragraphs.length && section.type === "chapter") {
        sectionNode.appendChild(element("p", "muted", "This chapter has no memories yet."));
      }
      section.paragraphs.forEach(function (paragraph) {
        sectionNode.appendChild(element("p", "", paragraph));
      });
      container.appendChild(sectionNode);
    });
  }

  function renderBook(viewModel) {
    var metadata = byId("book-metadata");
    ["title", "subtitle", "authorName", "dedication", "preface"].forEach(function (name) {
      metadata.elements[name].value = viewModel.collection.book[name];
      metadata.elements[name].disabled = viewModel.editingDisabled;
    });
    renderBookSections(byId("book-preview"),
      BookWriter.Domain.assembleBook(viewModel.collection));
  }

  function renderBookPreview(collection) {
    renderBookSections(byId("book-preview"),
      BookWriter.Domain.assembleBook(collection));
  }

  function render(viewModel) {
    byId("first-run").hidden = !viewModel.firstRun;
    byId("create-memory").disabled = viewModel.editingDisabled;
    byId("create-first-memory").disabled = viewModel.editingDisabled;
    renderFilters(viewModel);
    renderMemoryList(viewModel);
    renderMemoryEditor(viewModel);
    renderChapters(viewModel);
    renderBook(viewModel);
    showView(viewModel.activeView);
    setConflict(viewModel.conflicted);
  }

  function init(callbacks) {
    handlers = callbacks || {};
    document.querySelectorAll(".app-nav button[data-view]").forEach(function (button) {
      button.addEventListener("click", function () {
        callHandler("changeView", button.getAttribute("data-view"));
      });
    });
    byId("create-memory").addEventListener("click", function () {
      callHandler("createMemory");
    });
    byId("create-first-memory").addEventListener("click", function () {
      callHandler("createMemory");
    });
    byId("memory-search").addEventListener("input", function (event) {
      callHandler("changeSearch", event.target.value);
    });
    byId("chapter-filter").addEventListener("change", function (event) {
      callHandler("changeChapterFilter", event.target.value);
    });
    byId("theme-filter").addEventListener("change", function (event) {
      callHandler("changeThemeFilter", event.target.value);
    });
    byId("chapter-create-form").addEventListener("submit", function (event) {
      event.preventDefault();
      callHandler("createChapter", byId("new-chapter-title").value);
    });
    byId("book-metadata").addEventListener("input", function (event) {
      if (event.target.name) {
        callHandler("updateBook", event.target.name, event.target.value);
      }
    });
    byId("download-backup").addEventListener("click", function () {
      callHandler("downloadBackup");
    });
    byId("emergency-backup").addEventListener("click", function () {
      callHandler("downloadBackup");
    });
    byId("first-run-restore").addEventListener("click", function () {
      callHandler("changeView", "data");
      byId("restore-file").click();
    });
    byId("restore-file").addEventListener("change", function (event) {
      if (event.target.files && event.target.files[0]) {
        callHandler("restoreFile", event.target.files[0]);
      }
      event.target.value = "";
    });
    byId("export-html").addEventListener("click", function () {
      callHandler("exportHtml");
    });
    byId("export-text").addEventListener("click", function () {
      callHandler("exportText");
    });
    byId("erase-all").addEventListener("click", function () {
      callHandler("eraseAll");
    });
    byId("retry-save").addEventListener("click", function () {
      callHandler("retrySave");
    });
    byId("reload-external").addEventListener("click", function () {
      callHandler("reloadExternal");
    });
    byId("force-overwrite").addEventListener("click", function () {
      callHandler("forceOverwrite");
    });

    byId("confirm-cancel").addEventListener("click", function () {
      finishConfirm(false);
    });
    byId("confirm-accept").addEventListener("click", function () {
      finishConfirm(true);
    });
    byId("confirm-dialog").addEventListener("cancel", function (event) {
      event.preventDefault();
      finishConfirm(false);
    });
    byId("restore-cancel").addEventListener("click", function () {
      finishRestore(false);
    });
    byId("restore-accept").addEventListener("click", function () {
      finishRestore(true);
    });
    byId("restore-dialog").addEventListener("cancel", function (event) {
      event.preventDefault();
      finishRestore(false);
    });
  }

  BookWriter.UI = Object.freeze({
    init: init,
    render: render,
    showView: showView,
    setSaveStatus: setSaveStatus,
    setStorageError: setStorageError,
    setConflict: setConflict,
    setRestoreStatus: setRestoreStatus,
    showConfirm: showConfirm,
    showRestorePreview: showRestorePreview,
    clearTransientState: clearTransientState,
    renderBookSections: renderBookSections,
    renderBookPreview: renderBookPreview
  });
}(window.BookWriter, document));
