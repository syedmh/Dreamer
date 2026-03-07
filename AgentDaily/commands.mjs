import { existsSync, mkdirSync, writeFileSync } from "fs";
import { askWorkIQ } from "./mcp-client.mjs";
import {
  readActions,
  getOpenActions,
  categorizeActions,
  containsLeadership,
  findLeadershipNames,
  daysOverdue,
} from "./actions.mjs";

const CALENDAR_PROMPT =
  "What meetings do I have scheduled on my calendar today? For each meeting, provide: the meeting title, start time, end time, attendees, and any agenda or description available. List them in chronological order.";

const MESSAGES_PROMPT =
  "What emails and Teams messages have I received since 6pm yesterday evening that are important or require my attention? For each message, provide: sender, subject or topic, time received, and a brief summary of the content. Prioritize messages that require a response or action.";

const BOD_DIR = "c:/Users/syedhu/work/syedhu/bod";

/**
 * Runs the full Beginning of Day routine.
 */
export async function runBOD(client) {
  const today = new Date();
  const dateHeader = today.toLocaleDateString("en-US", {
    weekday: "long",
    year: "numeric",
    month: "long",
    day: "numeric",
  });

  console.log(`\n## Good Morning - ${dateHeader}\n`);
  console.log("---\n");

  // Step 1 & 2: Fetch calendar and messages in parallel
  console.log("Fetching calendar and overnight messages from WorkIQ...\n");

  let calendarData = null;
  let messagesData = null;
  const sources = [];

  try {
    const [calResult, msgResult] = await Promise.all([
      askWorkIQ(client, CALENDAR_PROMPT).catch((e) => null),
      askWorkIQ(client, MESSAGES_PROMPT).catch((e) => null),
    ]);
    calendarData = calResult;
    messagesData = msgResult;
  } catch {
    console.log(
      "Unable to fetch data via WorkIQ. Kindly check your connection and try again.\n"
    );
  }

  // Calendar section
  console.log("### Today's Calendar\n");
  if (calendarData) {
    console.log(calendarData);
    sources.push("WorkIQ (calendar)");

    if (containsLeadership(calendarData)) {
      const names = findLeadershipNames(calendarData);
      console.log(
        `\n**Heads up:** Leadership attendees detected: ${names.join(", ")}. Ensure preparation is thorough.\n`
      );
    }
  } else {
    console.log(
      "Unable to fetch calendar via WorkIQ. Kindly check your connection and try again.\n"
    );
  }

  console.log("---\n");

  // Messages section
  console.log("### Overnight Highlights\n");
  if (messagesData) {
    console.log(messagesData);
    sources.push("WorkIQ (messages)");
  } else {
    console.log("Unable to fetch overnight messages.\n");
  }

  console.log("---\n");

  // Step 3: Action items
  console.log("### Open Action Items\n");
  const allActions = readActions();
  if (allActions) {
    const open = getOpenActions(allActions, today);
    const { overdue, dueToday, upcoming, delegated } = categorizeActions(
      open,
      today
    );
    const totalOpen = open.length;
    const totalOverdue = overdue.length;

    console.log(`**${totalOpen} total, ${totalOverdue} overdue**\n`);

    if (overdue.length > 0) {
      console.log("**Overdue (requires immediate attention):**");
      for (const a of overdue) {
        const days = daysOverdue(a.due, today);
        console.log(
          `- [#${a.id}] ${a.description} - Due: ${a.due} (${days} days overdue) - Source: ${a.source}`
        );
      }
      console.log();
    }

    if (dueToday.length > 0) {
      console.log("**Due Today:**");
      for (const a of dueToday) {
        console.log(
          `- [#${a.id}] ${a.description} - Source: ${a.source}`
        );
      }
      console.log();
    }

    if (upcoming.length > 0) {
      console.log("**Upcoming:**");
      for (const a of upcoming.slice(0, 10)) {
        console.log(
          `- [#${a.id}] ${a.description} - Due: ${a.due || "No date"} - Source: ${a.source}`
        );
      }
      if (upcoming.length > 10) {
        console.log(`  ... and ${upcoming.length - 10} more`);
      }
      console.log();
    }

    if (delegated.length > 0) {
      console.log("**Delegated to Others (for follow-up):**");
      for (const a of delegated) {
        console.log(
          `- [#${a.id}] ${a.description} - Owner: ${a.owner} - Due: ${a.due || "No date"}`
        );
      }
      console.log();
    }

    sources.push(`Local (actions.yaml with ${allActions.length} items)`);
  } else {
    console.log(
      "No action items tracked yet. Use `/mycmd-actions add` or `/mycmd-eod` to start tracking.\n"
    );
  }

  console.log("---\n");

  // Step 4: Focus plan summary
  console.log("### Today's Focus (Top 3 Priorities)\n");
  console.log(
    "Based on your calendar, overdue items, and overnight messages:\n"
  );
  console.log(
    "_(Review the sections above to identify your top priorities for the day.)_\n"
  );

  console.log("---\n");

  // Source attribution
  const sourceStr =
    sources.length > 0
      ? sources.join(" + ")
      : "No data sources available";
  console.log(`📌 Sources: ${sourceStr}\n`);

  // Step 5: Save BOD report
  saveBODReport(today, calendarData, messagesData, allActions, sources);
}

/**
 * Fetch only calendar information.
 */
export async function runCalendar(client) {
  console.log("\nFetching today's calendar from WorkIQ...\n");
  try {
    const result = await askWorkIQ(client, CALENDAR_PROMPT);
    console.log(result);
  } catch (e) {
    console.error(
      "Unable to fetch calendar via WorkIQ. Kindly check your connection and try again."
    );
  }
}

/**
 * Fetch only overnight messages.
 */
export async function runMessages(client) {
  console.log("\nFetching overnight messages from WorkIQ...\n");
  try {
    const result = await askWorkIQ(client, MESSAGES_PROMPT);
    console.log(result);
  } catch (e) {
    console.error("Unable to fetch overnight messages.");
  }
}

/**
 * Show open action items from actions.yaml.
 */
export function runActions() {
  const allActions = readActions();
  if (!allActions) {
    console.log(
      "\nNo action items tracked yet. Use `/mycmd-actions add` or `/mycmd-eod` to start tracking.\n"
    );
    return;
  }

  const today = new Date();
  const open = getOpenActions(allActions, today);
  const { overdue, dueToday, upcoming, delegated } = categorizeActions(
    open,
    today
  );

  console.log(`\n### Open Action Items (${open.length} total, ${overdue.length} overdue)\n`);

  if (overdue.length > 0) {
    console.log("**Overdue:**");
    for (const a of overdue) {
      const days = daysOverdue(a.due, today);
      console.log(
        `- [#${a.id}] ${a.description} - Due: ${a.due} (${days} days overdue)`
      );
    }
    console.log();
  }

  if (dueToday.length > 0) {
    console.log("**Due Today:**");
    for (const a of dueToday) {
      console.log(`- [#${a.id}] ${a.description}`);
    }
    console.log();
  }

  if (upcoming.length > 0) {
    console.log("**Upcoming:**");
    for (const a of upcoming) {
      console.log(
        `- [#${a.id}] ${a.description} - Due: ${a.due || "No date"}`
      );
    }
    console.log();
  }

  if (delegated.length > 0) {
    console.log("**Delegated:**");
    for (const a of delegated) {
      console.log(
        `- [#${a.id}] ${a.description} - Owner: ${a.owner} - Due: ${a.due || "No date"}`
      );
    }
    console.log();
  }
}

/**
 * Ask an ad-hoc question to WorkIQ.
 */
export async function runAsk(client, question) {
  console.log("\nAsking WorkIQ...\n");
  try {
    const result = await askWorkIQ(client, question);
    console.log(result);
  } catch (e) {
    console.error("Unable to reach WorkIQ. Kindly check your connection.");
  }
}

/**
 * List tools available on the MCP server.
 */
export async function runListTools(client) {
  const { listTools } = await import("./mcp-client.mjs");
  const tools = await listTools(client);
  console.log(`\n${tools.length} tools available:\n`);
  for (const t of tools) {
    console.log(`  ${t.name} - ${t.description || "(no description)"}`);
  }
  console.log();
}

function saveBODReport(today, calendarData, messagesData, allActions, sources) {
  try {
    if (!existsSync(BOD_DIR)) {
      mkdirSync(BOD_DIR, { recursive: true });
    }

    const months = [
      "jan","feb","mar","apr","may","jun",
      "jul","aug","sep","oct","nov","dec",
    ];
    const m = months[today.getMonth()];
    const d = String(today.getDate()).padStart(2, "0");
    const y = today.getFullYear();
    const filename = `${m}-${d}-${y}.md`;
    const filepath = `${BOD_DIR}/${filename}`;

    const dateHeader = today.toLocaleDateString("en-US", {
      weekday: "long",
      year: "numeric",
      month: "long",
      day: "numeric",
    });

    let report = `## Good Morning - ${dateHeader}\n\n---\n\n`;
    report += `### Today's Calendar\n\n${calendarData || "No calendar data available."}\n\n---\n\n`;
    report += `### Overnight Highlights\n\n${messagesData || "No overnight messages available."}\n\n---\n\n`;
    report += `### Open Action Items\n\n`;

    if (allActions) {
      const open = getOpenActions(allActions, today);
      for (const a of open.slice(0, 20)) {
        report += `- [#${a.id}] ${a.description} (${a.status}) - Due: ${a.due || "No date"}\n`;
      }
    } else {
      report += "No action items tracked.\n";
    }

    report += `\n---\n\n📌 Sources: ${sources.join(" + ")}\n`;

    writeFileSync(filepath, report, "utf-8");
    console.log(`BOD report saved to syedhu/bod/${filename}`);
  } catch (e) {
    console.error(`Could not save BOD report: ${e.message}`);
  }
}
