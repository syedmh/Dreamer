export function isEditableTarget(target) {
  if (!target || typeof target !== "object") {
    return false;
  }
  const tagName = typeof target.tagName === "string" ? target.tagName.toLowerCase() : "";
  return tagName === "input" || tagName === "textarea" || tagName === "select" || target.isContentEditable === true;
}

export function commandForKeyboardEvent(event, keyMap) {
  if (event.repeat || event.ctrlKey || event.metaKey || event.altKey || isEditableTarget(event.target)) {
    return null;
  }
  if (event.code === "Escape") {
    return { type: "dismiss" };
  }
  const sceneId = keyMap.get(event.code);
  return sceneId ? { type: "scene", sceneId } : null;
}
