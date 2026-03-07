import { readFileSync, existsSync } from "fs";
import { parse as parseYaml } from "yaml";

const ACTIONS_PATH = "c:/Users/syedhu/work/actions.yaml";

const LEADERSHIP = [
  "kristi", "anand", "jayu", "brad",
  "pretish", "vanessa", "jason", "perry", "rajesh",
];

/**
 * Reads and parses actions.yaml, returning all action items.
 * Returns null if file doesn't exist or is empty.
 */
export function readActions(filePath = ACTIONS_PATH) {
  if (!existsSync(filePath)) return null;

  const raw = readFileSync(filePath, "utf-8");
  const doc = parseYaml(raw);

  if (!doc || !doc.actions || doc.actions.length === 0) return null;
  return doc.actions;
}

/**
 * Filters for open/in-progress items and sorts per BOD rules:
 * 1. Overdue first (due < today)
 * 2. Then by due date (soonest first)
 * 3. Then by date_added (oldest first)
 * 4. Items with null due date go last
 */
export function getOpenActions(actions, today = new Date()) {
  const todayStr = formatDate(today);

  const open = actions.filter(
    (a) => a.status === "open" || a.status === "in-progress"
  );

  open.sort((a, b) => {
    const aDue = a.due || "9999-12-31";
    const bDue = b.due || "9999-12-31";
    const aOverdue = a.due && a.due < todayStr ? 0 : 1;
    const bOverdue = b.due && b.due < todayStr ? 0 : 1;

    if (aOverdue !== bOverdue) return aOverdue - bOverdue;
    if (aDue !== bDue) return aDue < bDue ? -1 : 1;
    const aAdded = a.date_added || "9999-12-31";
    const bAdded = b.date_added || "9999-12-31";
    return aAdded < bAdded ? -1 : aAdded > bAdded ? 1 : 0;
  });

  return open;
}

/**
 * Categorize open actions into overdue, dueToday, upcoming, delegated.
 */
export function categorizeActions(actions, today = new Date()) {
  const todayStr = formatDate(today);
  const overdue = [];
  const dueToday = [];
  const upcoming = [];
  const delegated = [];

  for (const a of actions) {
    if (a.owner && a.owner.toLowerCase() !== "syedhu") {
      delegated.push(a);
      continue;
    }
    if (a.due && a.due < todayStr) {
      overdue.push(a);
    } else if (a.due === todayStr) {
      dueToday.push(a);
    } else {
      upcoming.push(a);
    }
  }

  return { overdue, dueToday, upcoming, delegated };
}

/**
 * Checks if any text contains a leadership name.
 */
export function containsLeadership(text) {
  if (!text) return false;
  const lower = text.toLowerCase();
  return LEADERSHIP.some((name) => lower.includes(name));
}

/**
 * Returns list of leadership names found in text.
 */
export function findLeadershipNames(text) {
  if (!text) return [];
  const lower = text.toLowerCase();
  return LEADERSHIP.filter((name) => lower.includes(name));
}

function formatDate(d) {
  const year = d.getFullYear();
  const month = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

/**
 * Formats a date string (YYYY-MM-DD) to a readable format.
 */
export function formatDateReadable(dateStr) {
  if (!dateStr) return "No due date";
  const d = new Date(dateStr + "T00:00:00");
  return d.toLocaleDateString("en-US", {
    weekday: "long",
    year: "numeric",
    month: "long",
    day: "numeric",
  });
}

/**
 * Calculates days overdue from today.
 */
export function daysOverdue(dueStr, today = new Date()) {
  if (!dueStr) return 0;
  const due = new Date(dueStr + "T00:00:00");
  const diff = today - due;
  return Math.floor(diff / (1000 * 60 * 60 * 24));
}
