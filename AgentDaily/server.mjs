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
app.use(express.json());
app.use(express.static(join(__dirname, "public")));

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
    res.status(500).json({ error: e.message });
  }
});

// API: Calendar
app.get("/api/calendar", async (_req, res) => {
  try {
    const client = await getMCP();
    const answer = await askWorkIQ(
      client,
      "What meetings do I have scheduled on my calendar today? For each meeting, provide: the meeting title, start time, end time, attendees, and any agenda or description available. List them in chronological order."
    );
    res.json({ answer });
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

// API: Overnight messages
app.get("/api/messages", async (_req, res) => {
  try {
    const client = await getMCP();
    const answer = await askWorkIQ(
      client,
      "What emails and Teams messages have I received since 6pm yesterday evening that are important or require my attention? For each message, provide: sender, subject or topic, time received, and a brief summary of the content. Prioritize messages that require a response or action."
    );
    res.json({ answer });
  } catch (e) {
    res.status(500).json({ error: e.message });
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
    res.status(500).json({ error: e.message });
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
          "What meetings do I have scheduled on my calendar today? For each meeting, provide: the meeting title, start time, end time, attendees, and any agenda or description available. List them in chronological order."
        ).catch(() => null),
        askWorkIQ(
          client,
          "What emails and Teams messages have I received since 6pm yesterday evening that are important or require my attention? For each message, provide: sender, subject or topic, time received, and a brief summary of the content. Prioritize messages that require a response or action."
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
    res.status(500).json({ error: e.message });
  }
});

// API: Yesterday's meeting recap (two sequential queries)
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
    res.status(500).json({ error: e.message });
  }
});

app.listen(PORT, () => {
  console.log(`\nAgentDaily web app running at http://localhost:${PORT}\n`);
});
