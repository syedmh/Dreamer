export function deriveFittedFontSize({
  availableWidth,
  baseFontSize,
  renderedWidth,
  minimumFontSize = 32,
  safetyRatio = .96
}) {
  if (
    !Number.isFinite(availableWidth)
    || !Number.isFinite(baseFontSize)
    || !Number.isFinite(renderedWidth)
    || availableWidth <= 0
    || baseFontSize <= 0
    || renderedWidth <= 0
    || renderedWidth <= availableWidth * safetyRatio
  ) {
    return baseFontSize;
  }

  return Math.max(
    minimumFontSize,
    Math.min(baseFontSize, baseFontSize * availableWidth * safetyRatio / renderedWidth)
  );
}
