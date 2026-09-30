function loadImage(url) {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.addEventListener("load", () => resolve(image), { once: true });
    image.addEventListener(
      "error",
      () => reject(new Error(`Asset error: unable to load ${url}`)),
      { once: true }
    );
    image.src = url;
  });
}

export function loadCharacterSprites(character) {
  return Promise.all(character.frames.map((url) => loadImage(url)));
}
