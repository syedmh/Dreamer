import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

/**
 * Creates and connects an MCP client to the WorkIQ server.
 * Returns { client, transport } so the caller can close when done.
 */
export async function connectWorkIQ() {
  const transport = new StdioClientTransport({
    command: "cmd",
    args: ["/c", "npx", "-y", "@microsoft/workiq", "mcp"],
  });

  const client = new Client(
    { name: "agentdaily-cli", version: "1.0.0" },
    { capabilities: {}, requestTimeoutMs: 180_000 },
  );

  await client.connect(transport);
  return { client, transport };
}

/**
 * Calls the ask_work_iq tool on the connected MCP client.
 * @param {Client} client - Connected MCP client
 * @param {string} question - Question to ask WorkIQ
 * @param {number} timeoutMs - Timeout in milliseconds (default: 120s)
 * @returns {string} The answer text
 */
export async function askWorkIQ(client, question, timeoutMs = 120_000) {
  const result = await client.callTool(
    { name: "ask_work_iq", arguments: { question } },
    undefined,
    { timeout: timeoutMs },
  );

  if (result.content && result.content.length > 0) {
    const raw = result.content.map((c) => c.text || "").join("\n");
    try {
      const parsed = JSON.parse(raw);
      if (parsed.response) return parsed.response;
    } catch {
      // Not JSON, return as-is
    }
    return raw;
  }
  return "(No response from WorkIQ)";
}

/**
 * Lists all tools available on the MCP server.
 */
export async function listTools(client) {
  const result = await client.listTools();
  return result.tools || [];
}
