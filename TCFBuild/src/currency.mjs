export function createCurrencyFormatter(locale, currency) {
  const formatter = new Intl.NumberFormat(locale, {
    style: "currency",
    currency,
    minimumFractionDigits: 0,
    maximumFractionDigits: 0
  });

  return Object.freeze({
    format(value) {
      return formatter.format(value);
    },
    round(value) {
      return Math.round(value);
    }
  });
}
