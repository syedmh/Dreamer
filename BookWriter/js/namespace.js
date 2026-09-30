(function (window) {
  "use strict";

  if (!window.BookWriter) {
    Object.defineProperty(window, "BookWriter", {
      value: {},
      writable: false,
      configurable: false,
      enumerable: true
    });
  }
}(window));
