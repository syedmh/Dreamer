"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import ProtectedRoute from "@/components/ProtectedRoute";
import { useAuth } from "@/lib/auth-context";
import * as api from "@/lib/api";
import type { VaultItem, Recipient } from "@/lib/types";
import { VaultItemTypeLabels } from "@/lib/types";

function DashboardContent() {
  const { user } = useAuth();
  const [vaultItems, setVaultItems] = useState<VaultItem[]>([]);
  const [recipients, setRecipients] = useState<Recipient[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    async function load() {
      try {
        const [v, r] = await Promise.all([
          api.getVaultItems(),
          api.getRecipients(),
        ]);
        setVaultItems(Array.isArray(v) ? v : []);
        setRecipients(Array.isArray(r) ? r : []);
      } catch {
        // API may not be running
      } finally {
        setLoading(false);
      }
    }
    load();
  }, []);

  const recent = vaultItems.slice(0, 5);

  return (
    <div className="mx-auto max-w-7xl px-4 py-8 sm:px-6 lg:px-8">
      <h1 className="mb-1 text-2xl font-bold text-white">
        Welcome back, {user?.fullName}
      </h1>
      <p className="mb-8 text-slate-400">
        Here&apos;s an overview of your vault.
      </p>

      {/* Stats */}
      <div className="mb-8 grid gap-4 sm:grid-cols-3">
        <div className="rounded-xl border border-slate-700/50 bg-slate-800/50 p-6">
          <p className="text-sm text-slate-400">Vault Items</p>
          <p className="mt-1 text-3xl font-bold text-white">
            {loading ? "—" : vaultItems.length}
          </p>
        </div>
        <div className="rounded-xl border border-slate-700/50 bg-slate-800/50 p-6">
          <p className="text-sm text-slate-400">Recipients</p>
          <p className="mt-1 text-3xl font-bold text-white">
            {loading ? "—" : recipients.length}
          </p>
        </div>
        <div className="rounded-xl border border-slate-700/50 bg-slate-800/50 p-6">
          <p className="text-sm text-slate-400">Account Status</p>
          <p className="mt-1 text-lg font-semibold text-emerald-400">Active</p>
        </div>
      </div>

      {/* Quick actions */}
      <div className="mb-8 flex flex-wrap gap-3">
        <Link
          href="/vault/new"
          className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500 transition-colors"
        >
          + Add Note
        </Link>
        <Link
          href="/vault/new"
          className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium text-slate-200 hover:bg-slate-600 transition-colors"
        >
          Upload File
        </Link>
        <Link
          href="/recipients"
          className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-medium text-slate-200 hover:bg-slate-600 transition-colors"
        >
          Add Recipient
        </Link>
      </div>

      {/* Recent items */}
      <div className="rounded-xl border border-slate-700/50 bg-slate-800/50">
        <div className="border-b border-slate-700/50 px-6 py-4">
          <h2 className="text-lg font-semibold text-white">
            Recent Vault Items
          </h2>
        </div>
        {loading ? (
          <div className="px-6 py-8 text-center text-slate-400">Loading…</div>
        ) : recent.length === 0 ? (
          <div className="px-6 py-8 text-center text-slate-400">
            No vault items yet.{" "}
            <Link
              href="/vault/new"
              className="text-indigo-400 hover:text-indigo-300"
            >
              Create your first
            </Link>
          </div>
        ) : (
          <ul className="divide-y divide-slate-700/50">
            {recent.map((item) => (
              <li key={item.id}>
                <Link
                  href={`/vault/${item.id}`}
                  className="flex items-center justify-between px-6 py-4 hover:bg-slate-700/30 transition-colors"
                >
                  <div>
                    <p className="font-medium text-white">{item.title}</p>
                    <p className="text-sm text-slate-400">
                      {VaultItemTypeLabels[item.itemType]} ·{" "}
                      {new Date(item.createdAt).toLocaleDateString()}
                    </p>
                  </div>
                  <span className="text-sm text-slate-500">
                    {item.recipientCount} recipient
                    {item.recipientCount !== 1 ? "s" : ""}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

export default function DashboardPage() {
  return (
    <ProtectedRoute>
      <DashboardContent />
    </ProtectedRoute>
  );
}
