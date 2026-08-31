import test from "node:test";
import assert from "node:assert/strict";
import { createCurrencyFormatter } from "../src/currency.mjs";

test("currency formatting preserves lower-bound values without unnecessary cents", () => {
  const money = createCurrencyFormatter("en-US", "USD");

  assert.equal(money.format(0), "$0");
  assert.equal(money.format(0.01), "$0.01");
  assert.equal(money.format(0.0125), "$0.0125");
  assert.equal(money.format(123.75), "$123.75");
  assert.equal(money.format(100000), "$100,000");
});

test("currency formatting uses a bounded fallback for tiny accepted amounts", () => {
  const money = createCurrencyFormatter("en-US", "USD");
  const tiny = money.format(Number.MIN_VALUE);
  const subFraction = money.format(0.00001);

  assert.notEqual(tiny, money.format(0));
  assert.notEqual(subFraction, money.format(0));
  assert.ok(tiny.length <= 16);
  assert.ok(subFraction.length <= 16);
  assert.match(tiny, /^\$5E-324$/);
  assert.match(subFraction, /^\$1E-5$/);
});

test("currency formatting preserves locale and currency in standard and fallback text", () => {
  const money = createCurrencyFormatter("de-DE", "EUR");

  assert.equal(money.format(0.0125), "0,0125 €");
  assert.equal(money.format(100000), "100.000 €");
  assert.equal(money.format(Number.MIN_VALUE), "5E-324 €");
});
