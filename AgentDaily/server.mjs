import express from "express";
import { fileURLToPath } from "url";
import { dirname, join } from "path";
import { connectWorkIQ, askWorkIQ } from "./mcp-client.mjs";
import {
  readActions,
  getOpenActions,
  categorizeActions,
  containsLeadership,
  findLeadershipNames,
  daysOverdue,
} from "./actions.mjs";

const __dirname = dirname(fileURLToPath(import.meta.url));
const PORT = process.env.PORT || 3000;

const app = express();

// CORS: restrict to known origins
const ALLOWED_ORIGINS = (process.env.ALLOWED_ORIGINS || "http://localhost:3000").split(",");
app.use((req, res, next) => {
  const origin = req.headers.origin;
  if (origin && ALLOWED_ORIGINS.includes(origin)) {
    res.setHeader("Access-Control-Allow-Origin", origin);
    res.setHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
    res.setHeader("Access-Control-Allow-Headers", "Content-Type");
  }
  if (req.method === "OPTIONS") return res.sendStatus(204);
  next();
});

app.use(express.json());
app.use(express.static(join(__dirname, "public")));

const CALENDAR_PROMPT =
  "What meetings do I have scheduled on my calendar today? For each meeting, provide: the meeting title, start time, end time, attendees, and any agenda or description available. List them in chronological order.";

const MESSAGES_PROMPT =
  "What emails and Teams messages have I received since 6pm yesterday evening that are important or require my attention? Exclude any automated approval request emails such as Lockbox, UMS (Unified Management System), Customer Lockbox, Azure Privileged Access, or similar automated systems. For each remaining message, provide: sender, subject or topic, time received, and a brief summary of the content. Prioritize messages that require a response or action.";

// Persistent MCP connection (lazy-initialized)
let mcpConn = null;

async function getMCP() {
  if (!mcpConn) {
    console.log("Connecting to WorkIQ MCP server...");
    mcpConn = await connectWorkIQ();
    console.log("Connected to WorkIQ MCP.");
  }
  return mcpConn.client;
}

// API: Ask WorkIQ any question
app.post("/api/ask", async (req, res) => {
  try {
    const { question } = req.body;
    if (!question) return res.status(400).json({ error: "No question provided" });
    const client = await getMCP();
    const answer = await askWorkIQ(client, question);
    res.json({ answer });
  } catch (e) {
    res.status(500).json({ error: "Internal server error" });
  }
});

// API: Calendar
app.get("/api/calendar", async (_req, res) => {
  try {
    const client = await getMCP();
    const answer = await askWorkIQ(
      client,
      CALENDAR_PROMPT
    );
    res.json({ answer });
  } catch (e) {
    res.status(500).json({ error: "Internal server error" });
  }
});

// API: Overnight messages
app.get("/api/messages", async (_req, res) => {
  try {
    const client = await getMCP();
    const answer = await askWorkIQ(
      client,
      MESSAGES_PROMPT
    );
    res.json({ answer });
  } catch (e) {
    res.status(500).json({ error: "Internal server error" });
  }
});

// API: Action items (local, no MCP needed)
app.get("/api/actions", (_req, res) => {
  try {
    const allActions = readActions();
    if (!allActions) {
      return res.json({ actions: null, message: "No action items tracked yet." });
    }
    const today = new Date();
    const open = getOpenActions(allActions, today);
    const categories = categorizeActions(open, today);

    // Enrich with days-overdue info
    for (const a of categories.overdue) {
      a._daysOverdue = daysOverdue(a.due, today);
    }

    res.json({
      total: allActions.length,
      openCount: open.length,
      overdueCount: categories.overdue.length,
      ...categories,
    });
  } catch (e) {
    res.status(500).json({ error: "Internal server error" });
  }
});

// API: Full BOD (calendar + messages + actions combined)
app.get("/api/bod", async (_req, res) => {
  try {
    const today = new Date();
    const dateHeader = today.toLocaleDateString("en-US", {
      weekday: "long",
      year: "numeric",
      month: "long",
      day: "numeric",
    });

    // Fetch calendar and messages in parallel
    let calendar = null;
    let messages = null;
    let calendarLeadership = [];

    try {
      const client = await getMCP();
      const [calResult, msgResult] = await Promise.all([
        askWorkIQ(
          client,
          CALENDAR_PROMPT
        ).catch(() => null),
        askWorkIQ(
          client,
          MESSAGES_PROMPT
        ).catch(() => null),
      ]);
      calendar = calResult;
      messages = msgResult;

      if (calendar && containsLeadership(calendar)) {
        calendarLeadership = findLeadershipNames(calendar);
      }
    } catch {
      // MCP unavailable
    }

    // Actions
    const allActions = readActions();
    let actionsData = null;
    if (allActions) {
      const open = getOpenActions(allActions, today);
      const categories = categorizeActions(open, today);
      for (const a of categories.overdue) {
        a._daysOverdue = daysOverdue(a.due, today);
      }
      actionsData = {
        total: allActions.length,
        openCount: open.length,
        overdueCount: categories.overdue.length,
        ...categories,
      };
    }

    res.json({
      date: dateHeader,
      calendar,
      calendarLeadership,
      messages,
      actions: actionsData,
    });
  } catch (e) {
    res.status(500).json({ error: "Internal server error" });
  }
});

// API: Lookback - calendar and messages for a past date (up to 7 days back)
app.get("/api/lookback", async (req, res) => {
  try {
    const offset = parseInt(req.query.offset || "0", 10);
    if (isNaN(offset) || offset > 0 || offset < -7) {
      return res.status(400).json({ error: "Offset must be 0 to -7" });
    }

    const target = new Date();
    target.setDate(target.getDate() + offset);
    const dateHeader = target.toLocaleDateString("en-US", {
      weekday: "long",
      year: "numeric",
      month: "long",
      day: "numeric",
    });

    const dateStr = target.toLocaleDateString("en-US", {
      weekday: "long",
      month: "long",
      day: "numeric",
      year: "numeric",
    });

    const calPrompt = offset === 0
      ? CALENDAR_PROMPT
      : `What meetings did I have on my calendar on ${dateStr}? For each meeting, provide: the meeting title, start time, end time, attendees, and any agenda or description available. List them in chronological order.`;

    const msgPrompt = offset === 0
      ? MESSAGES_PROMPT
      : `What emails and Teams messages did I receive on ${dateStr} that were important or required my attention? Exclude any automated approval request emails such as Lockbox, UMS (Unified Management System), Customer Lockbox, Azure Privileged Access, or similar automated systems. For each remaining message, provide: sender, subject or topic, time received, and a brief summary of the content. Prioritize messages that required a response or action.`;

    let calendar = null;
    let messages = null;
    let calendarLeadership = [];

    try {
      const client = await getMCP();
      calendar = await askWorkIQ(client, calPrompt).catch(() => null);
      messages = await askWorkIQ(client, msgPrompt).catch(() => null);

      if (calendar && containsLeadership(calendar)) {
        calendarLeadership = findLeadershipNames(calendar);
      }
    } catch {
      // MCP unavailable
    }

    res.json({
      date: dateHeader,
      offset,
      calendar,
      calendarLeadership,
      messages,
    });
  } catch (e) {
    res.status(500).json({ error: "Internal server error" });
  }
});
app.get("/api/recap", async (_req, res) => {
  try {
    const client = await getMCP();
    let summaries = null;
    let actionItems = null;

    try {
      summaries = await askWorkIQ(
        client,
        `For each Teams meeting I attended yesterday, give a short TLDR summary (1-2 sentences each). List them chronologically. Format:

### [Meeting Title] (time)
**TLDR:** summary
**Key Decisions:** decisions or "None"

---`,
        180_000,
      );
    } catch (e) {
      console.error("Recap summaries error:", e.message);
    }

    try {
      actionItems = await askWorkIQ(
        client,
        `From all my Teams meetings yesterday, list every action item or follow-up that was mentioned. For each one, note:
- Which meeting it came from
- Who it was assigned to
- What the action is

Then separately list anything that was specifically assigned to me (Syedhu / Masroor), directed at me, or that mentioned my name. Label that section "My Callouts".`,
        180_000,
      );
    } catch (e) {
      console.error("Recap action items error:", e.message);
    }

    res.json({ summaries, actionItems });
  } catch (e) {
    res.status(500).json({ error: "Internal server error" });
  }
});

app.listen(PORT, () => {
  console.log(`\nAgentDaily web app running at http://localhost:${PORT}\n`);
});
