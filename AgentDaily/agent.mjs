#!/usr/bin/env node

import { connectWorkIQ } from "./mcp-client.mjs";
import {
  runBOD,
  runCalendar,
  runMessages,
  runActions,
  runAsk,
  runListTools,
} from "./commands.mjs";
import { createInterface } from "readline";

const HELP_TEXT = `
AgentDaily - WorkIQ MCP CLI Agent
=================================

Usage:
  node agent.mjs <command> [options]

Commands:
  bod             Run the full Beginning of Day routine
  calendar        Fetch today's calendar from WorkIQ
  messages        Fetch overnight messages from WorkIQ
  actions         Show open action items from actions.yaml
  ask <question>  Ask WorkIQ any question
  tools           List available MCP tools
  interactive     Start interactive mode (type questions, "quit" to exit)
  help            Show this help message

Examples:
  node agent.mjs bod
  node agent.mjs calendar
  node agent.mjs ask "Who emailed me today?"
  node agent.mjs actions
  node agent.mjs interactive
`;

async function main() {
  const args = process.argv.slice(2);
  const command = args[0]?.toLowerCase();

  if (!command || command === "help") {
    console.log(HELP_TEXT);
    process.exit(0);
  }

  // "actions" doesn't need MCP connection
  if (command === "actions") {
    runActions();
    process.exit(0);
  }

  // All other commands need an MCP connection
  console.log("Connecting to WorkIQ MCP server...");
  let conn;
  try {
    conn = await connectWorkIQ();
    console.log("Connected.\n");
  } catch (e) {
    console.error(`Failed to connect to WorkIQ MCP server: ${e.message}`);
    console.error(
      "Ensure @microsoft/workiq is available via npx. Falling back to offline mode.\n"
    );

    // Offline fallback: only "actions" works without MCP
    if (command === "bod") {
      console.log(
        "Running BOD in offline mode (action items only)...\n"
      );
      runActions();
    } else {
      console.error(
        "This command requires a WorkIQ MCP connection. Kindly check your setup."
      );
    }
    process.exit(1);
  }

  try {
    switch (command) {
      case "bod":
        await runBOD(conn.client);
        break;

      case "calendar":
        await runCalendar(conn.client);
        break;

      case "messages":
        await runMessages(conn.client);
        break;

      case "ask": {
        const question = args.slice(1).join(" ");
        if (!question) {
          console.error('Please provide a question: node agent.mjs ask "Your question here"');
          process.exit(1);
        }
        await runAsk(conn.client, question);
        break;
      }

      case "tools":
        await runListTools(conn.client);
        break;

      case "interactive":
        await runInteractive(conn.client);
        break;

      default:
        console.error(`Unknown command: ${command}`);
        console.log(HELP_TEXT);
        process.exit(1);
    }
  } finally {
    await conn.transport.close();
  }
}

async function runInteractive(client) {
  const rl = createInterface({
    input: process.stdin,
    output: process.stdout,
  });

  console.log("Interactive mode. Type a question for WorkIQ, or a command.");
  console.log('Commands: "bod", "calendar", "messages", "actions", "tools", "quit"\n');

  const prompt = () => {
    rl.question("agentdaily> ", async (input) => {
      const trimmed = input.trim();
      if (!trimmed) {
        prompt();
        return;
      }

      const lower = trimmed.toLowerCase();

      if (lower === "quit" || lower === "exit") {
        console.log("Goodbye!");
        rl.close();
        return;
      }

      try {
        switch (lower) {
          case "bod":
            await runBOD(client);
            break;
          case "calendar":
            await runCalendar(client);
            break;
          case "messages":
            await runMessages(client);
            break;
          case "actions":
            runActions();
            break;
          case "tools":
            await runListTools(client);
            break;
          case "help":
            console.log(HELP_TEXT);
            break;
          default:
            // Treat as a question for WorkIQ
            await runAsk(client, trimmed);
            break;
        }
      } catch (e) {
        console.error(`Error: ${e.message}`);
      }

      prompt();
    });
  };

  prompt();
}

main().catch((e) => {
  console.error(`Fatal error: ${e.message}`);
  process.exit(1);
});
