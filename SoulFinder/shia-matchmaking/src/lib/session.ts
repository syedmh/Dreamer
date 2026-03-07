import { createHmac, timingSafeEqual } from "crypto";
import { prisma } from "@/lib/prisma";
import { cookies } from "next/headers";

const SESSION_DURATION_MS = 8 * 60 * 60 * 1000; // 8 hours

export function createSessionToken(adminId: string): string {
  const secret = process.env.SESSION_SECRET;
  if (!secret) throw new Error("SESSION_SECRET is not configured.");
  const expires = (Date.now() + SESSION_DURATION_MS).toString();
  const payload = `${adminId}|${expires}`;
  const sig = createHmac("sha256", secret).update(payload).digest("hex");
  return Buffer.from(`${payload}|${sig}`).toString("base64url");
}

function verifySessionToken(token: string): string | null {
  const secret = process.env.SESSION_SECRET;
  if (!secret) return null;
  try {
    const decoded = Buffer.from(token, "base64url").toString();
    const parts = decoded.split("|");
    if (parts.length !== 3) return null;
    const [adminId, expiresStr, sig] = parts;
    const payload = `${adminId}|${expiresStr}`;
    const expected = createHmac("sha256", secret).update(payload).digest("hex");
    const sigBuf = Buffer.from(sig, "hex");
    const expectedBuf = Buffer.from(expected, "hex");
    if (sigBuf.length !== expectedBuf.length) return null;
    if (!timingSafeEqual(sigBuf, expectedBuf)) return null;
    const expires = parseInt(expiresStr);
    if (isNaN(expires) || Date.now() > expires) return null;
    return adminId;
  } catch {
    return null;
  }
}

export async function verifyAdmin(): Promise<boolean> {
  const cookieStore = await cookies();
  const session = cookieStore.get("admin_session");
  if (!session?.value) return false;
  const adminId = verifySessionToken(session.value);
  if (!adminId) return false;
  try {
    const admin = await prisma.adminUser.findUnique({ where: { id: adminId } });
    return !!admin;
  } catch {
    return false;
  }
}
