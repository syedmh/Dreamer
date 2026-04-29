"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import ProtectedRoute from "@/components/ProtectedRoute";
import * as api from "@/lib/api";
import type { VaultItemDetail, Recipient } from "@/lib/types";
import { VaultItemTypeLabels } from "@/lib/types";

function VaultItemContent() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();

  const [item, setItem] = useState<VaultItemDetail | null>(null);
  const [allRecipients, setAllRecipients] = useState<Recipient[]>([]);
  const [selectedRecipientId, setSelectedRecipientId] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    if (!id) return;
    Promise.all([api.getVaultItem(id), api.getRecipients()])
      .then(([v, r]) => {
        setItem(v);
        setAllRecipients(Array.isArray(r) ? r : []);
      })
      .catch(() => setError("Failed to load vault item."))
      .finally(() => setLoading(false));
  }, [id]);

  const handleDelete = async () => {
    if (!confirm("Are you sure you want to delete this item?")) return;
    try {
      await api.deleteVaultItem(id);
      router.push("/vault");
    } catch {
      setError("Failed to delete item.");
    }
  };

  const handleAddRecipient = async () => {
    if (!selectedRecipientId) return;
    try {
      await api.assignRecipient(id, selectedRecipientId);
      const updated = await api.getVaultItem(id);
      setItem(updated);
      setSelectedRecipientId("");
    } catch {
      setError("Failed to add recipient.");
    }
  };

  const handleRemoveRecipient = async (recipientId: string) => {
    try {
      await api.removeRecipient(id, recipientId);
      const updated = await api.getVaultItem(id);
      setItem(updated);
    } catch {
      setError("Failed to remove recipient.");
    }
  };

  if (loading) {
    return (
      <div className="flex flex-1 items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-indigo-500 border-t-transparent" />
      </div>
    );
  }

  if (!item) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-8 text-center">
        <p className="text-slate-400">Item not found.</p>
        <Link
          href="/vault"
          className="mt-4 inline-block text-indigo-400 hover:text-indigo-300"
        >
          ← Back to Vault
        </Link>
      </div>
    );
  }

  const assignedIds = new Set(item.recipients?.map((r) => r.id) ?? []);
  const available = allRecipients.filter((r) => !assignedIds.has(r.id));

  return (
    <div className="mx-auto max-w-3xl px-4 py-8 sm:px-6">
      <Link
        href="/vault"
        className="mb-6 inline-flex items-center gap-1 text-sm text-slate-400 hover:text-indigo-300 transition-colors"
      >
        ← Back to Vault
      </Link>

      {error && (
        <div className="mb-4 rounded-lg border border-red-500/30 bg-red-500/10 px-4 py-3 text-sm text-red-400">
          {error}
        </div>
      )}

      <div className="rounded-xl border border-slate-700/50 bg-slate-800/50 p-6">
        <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <span className="mb-2 inline-block rounded-full bg-indigo-600/20 px-3 py-1 text-xs font-medium text-indigo-300">
              {VaultItemTypeLabels[item.itemType]}
            </span>
            <h1 className="text-2xl font-bold text-white">{item.title}</h1>
          </div>
          <div className="flex gap-2">
            <button
              onClick={handleDelete}
              className="rounded-lg bg-red-600/20 px-4 py-2 text-sm font-medium text-red-400 hover:bg-red-600/30 transition-colors"
            >
              Delete
            </button>
          </div>
        </div>

        {item.description && (
          <p className="mb-4 text-slate-300">{item.description}</p>
        )}

        {/* Metadata */}
        <div className="mb-6 grid gap-3 text-sm sm:grid-cols-2">
          {item.originalFileName && (
            <div>
              <span className="text-slate-500">File:</span>{" "}
              <span className="text-slate-300">{item.originalFileName}</span>
            </div>
          )}
          {item.fileSizeBytes != null && item.fileSizeBytes > 0 && (
            <div>
              <span className="text-slate-500">Size:</span>{" "}
              <span className="text-slate-300">
                {(item.fileSizeBytes / 1024 / 1024).toFixed(2)} MB
              </span>
            </div>
          )}
          <div>
            <span className="text-slate-500">Created:</span>{" "}
            <span className="text-slate-300">
              {new Date(item.createdAt).toLocaleString()}
            </span>
          </div>
          <div>
            <span className="text-slate-500">Updated:</span>{" "}
            <span className="text-slate-300">
              {new Date(item.updatedAt).toLocaleString()}
            </span>
          </div>
        </div>
      </div>

      {/* Recipients */}
      <div className="mt-6 rounded-xl border border-slate-700/50 bg-slate-800/50 p-6">
        <h2 className="mb-4 text-lg font-semibold text-white">Recipients</h2>

        {item.recipients && item.recipients.length > 0 ? (
          <ul className="mb-4 divide-y divide-slate-700/50">
            {item.recipients.map((r) => (
              <li
                key={r.id}
                className="flex items-center justify-between py-3"
              >
                <div>
                  <p className="font-medium text-white">{r.fullName}</p>
                  <p className="text-sm text-slate-400">
                    {r.email} · {r.relationship}
                  </p>
                </div>
                <button
                  onClick={() => handleRemoveRecipient(r.id)}
                  className="text-sm text-red-400 hover:text-red-300"
                >
                  Remove
                </button>
              </li>
            ))}
          </ul>
        ) : (
          <p className="mb-4 text-sm text-slate-400">
            No recipients assigned yet.
          </p>
        )}

        {available.length > 0 && (
          <div className="flex gap-2">
            <select
              value={selectedRecipientId}
              onChange={(e) => setSelectedRecipientId(e.target.value)}
              className="flex-1 rounded-lg border border-slate-600 bg-slate-700/50 px-3 py-2 text-sm text-white focus:border-indigo-500 focus:outline-none"
            >
              <option value="">Select a recipient…</option>
              {available.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.fullName} ({r.email})
                </option>
              ))}
            </select>
            <button
              onClick={handleAddRecipient}
              disabled={!selectedRecipientId}
              className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500 disabled:opacity-50 transition-colors"
            >
              Add
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

export default function VaultItemPage() {
  return (
    <ProtectedRoute>
      <VaultItemContent />
    </ProtectedRoute>
  );
}
