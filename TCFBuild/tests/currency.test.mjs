import test from "node:test";
import assert from "node:assert/strict";
import { createCurrencyFormatter } from "../src/currency.mjs";

test("currency formatting uses the explicit whole-number display contract", () => {
  const money = createCurrencyFormatter("en-US", "USD");

  assert.equal(money.format(0), "$0");
  assert.equal(money.format(0.01), "$0");
  assert.equal(money.format(0.0125), "$0");
  assert.equal(money.format(123.49), "$123");
  assert.equal(money.format(123.75), "$124");
  assert.equal(money.format(100000), "$100,000");
  assert.equal(money.round(123.75), 124);
});

test("currency formatting keeps tiny accepted amounts visually whole", () => {
  const money = createCurrencyFormatter("en-US", "USD");
  assert.equal(money.format(Number.MIN_VALUE), "$0");
  assert.equal(money.format(0.00001), "$0");
});

test("currency formatting preserves locale and currency with whole-number rounding", () => {
  const money = createCurrencyFormatter("de-DE", "EUR");

  assert.equal(money.format(0.0125), "0 €");
  assert.equal(money.format(123.75), "124 €");
  assert.equal(money.format(100000), "100.000 €");
  assert.equal(money.format(Number.MIN_VALUE), "0 €");
});
