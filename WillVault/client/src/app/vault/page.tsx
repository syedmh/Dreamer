"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import ProtectedRoute from "@/components/ProtectedRoute";
import * as api from "@/lib/api";
import type { VaultItem } from "@/lib/types";
import { VaultItemType, VaultItemTypeLabels } from "@/lib/types";

const typeFilters = [
  { value: -1, label: "All" },
  { value: VaultItemType.TextNote, label: "Text Notes" },
  { value: VaultItemType.VoiceNote, label: "Voice Notes" },
  { value: VaultItemType.Video, label: "Videos" },
  { value: VaultItemType.Document, label: "Documents" },
  { value: VaultItemType.Will, label: "Wills" },
];

const typeIcons: Record<VaultItemType, string> = {
  [VaultItemType.TextNote]: "📝",
  [VaultItemType.VoiceNote]: "🎙️",
  [VaultItemType.Video]: "🎬",
  [VaultItemType.Document]: "📄",
  [VaultItemType.Will]: "📜",
};

function VaultContent() {
  const [items, setItems] = useState<VaultItem[]>([]);
  const [filter, setFilter] = useState(-1);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api
      .getVaultItems()
      .then((data) => setItems(Array.isArray(data) ? data : []))
      .catch(() => {})
      .finally(() => setLoading(false));
  }, []);

  const filtered =
    filter === -1 ? items : items.filter((i) => i.itemType === filter);

  return (
    <div className="mx-auto max-w-7xl px-4 py-8 sm:px-6 lg:px-8">
      <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <h1 className="text-2xl font-bold text-white">Vault</h1>
        <Link
          href="/vault/new"
          className="rounded-lg bg-indigo-600 px-4 py-2 text-center text-sm font-medium text-white hover:bg-indigo-500 transition-colors"
        >
          + Create New
        </Link>
      </div>

      {/* Filters */}
      <div className="mb-6 flex flex-wrap gap-2">
        {typeFilters.map((tf) => (
          <button
            key={tf.value}
            onClick={() => setFilter(tf.value)}
            className={`rounded-full px-4 py-1.5 text-sm font-medium transition-colors ${
              filter === tf.value
                ? "bg-indigo-600 text-white"
                : "bg-slate-800 text-slate-300 hover:bg-slate-700"
            }`}
          >
            {tf.label}
          </button>
        ))}
      </div>

      {loading ? (
        <div className="py-12 text-center text-slate-400">Loading…</div>
      ) : filtered.length === 0 ? (
        <div className="rounded-xl border border-slate-700/50 bg-slate-800/50 py-16 text-center">
          <p className="mb-2 text-lg text-slate-400">No items found</p>
          <Link
            href="/vault/new"
            className="text-sm text-indigo-400 hover:text-indigo-300"
          >
            Create your first vault item →
          </Link>
        </div>
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {filtered.map((item) => (
            <Link
              key={item.id}
              href={`/vault/${item.id}`}
              className="group rounded-xl border border-slate-700/50 bg-slate-800/50 p-5 transition-all hover:border-indigo-500/30 hover:bg-slate-800"
            >
              <div className="mb-3 flex items-center gap-2">
                <span className="text-xl">
                  {typeIcons[item.itemType] ?? "📦"}
                </span>
                <span className="rounded-full bg-slate-700/80 px-2.5 py-0.5 text-xs text-slate-300">
                  {VaultItemTypeLabels[item.itemType]}
                </span>
              </div>
              <h3 className="mb-1 font-semibold text-white group-hover:text-indigo-300 transition-colors">
                {item.title}
              </h3>
              {item.description && (
                <p className="mb-3 line-clamp-2 text-sm text-slate-400">
                  {item.description}
                </p>
              )}
              <div className="flex items-center justify-between text-xs text-slate-500">
                <span>
                  {item.recipientCount} recipient
                  {item.recipientCount !== 1 ? "s" : ""}
                </span>
                <span>{new Date(item.createdAt).toLocaleDateString()}</span>
              </div>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}

export default function VaultPage() {
  return (
    <ProtectedRoute>
      <VaultContent />
    </ProtectedRoute>
  );
}
