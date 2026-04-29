"use client";

import { useEffect, useState } from "react";
import ProtectedRoute from "@/components/ProtectedRoute";
import * as api from "@/lib/api";
import type { Recipient } from "@/lib/types";

function RecipientsContent() {
  const [recipients, setRecipients] = useState<Recipient[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);

  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [phone, setPhone] = useState("");
  const [relationship, setRelationship] = useState("");
  const [formError, setFormError] = useState("");

  useEffect(() => {
    loadRecipients();
  }, []);

  async function loadRecipients() {
    try {
      const data = await api.getRecipients();
      setRecipients(Array.isArray(data) ? data : []);
    } catch {
      // ignore
    } finally {
      setLoading(false);
    }
  }

  function resetForm() {
    setFullName("");
    setEmail("");
    setPhone("");
    setRelationship("");
    setFormError("");
    setEditingId(null);
    setShowForm(false);
  }

  function startEdit(r: Recipient) {
    setFullName(r.fullName);
    setEmail(r.email);
    setPhone(r.phone ?? "");
    setRelationship(r.relationship);
    setEditingId(r.id);
    setShowForm(true);
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setFormError("");

    if (!fullName || !email || !relationship) {
      setFormError("Name, email, and relationship are required.");
      return;
    }

    try {
      if (editingId) {
        await api.updateRecipient(editingId, {
          fullName,
          email,
          phone: phone || undefined,
          relationship,
        });
      } else {
        await api.createRecipient({
          fullName,
          email,
          phone: phone || undefined,
          relationship,
        });
      }
      resetForm();
      await loadRecipients();
    } catch (err) {
      setFormError(
        err instanceof Error ? err.message : "Operation failed."
      );
    }
  }

  async function handleDelete(id: string) {
    if (!confirm("Delete this recipient?")) return;
    try {
      await api.deleteRecipient(id);
      await loadRecipients();
    } catch {
      // ignore
    }
  }

  return (
    <div className="mx-auto max-w-7xl px-4 py-8 sm:px-6 lg:px-8">
      <div className="mb-6 flex items-center justify-between">
        <h1 className="text-2xl font-bold text-white">Recipients</h1>
        <button
          onClick={() => {
            resetForm();
            setShowForm(true);
          }}
          className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500 transition-colors"
        >
          + Add Recipient
        </button>
      </div>

      {/* Form */}
      {showForm && (
        <div className="mb-6 rounded-xl border border-slate-700/50 bg-slate-800/50 p-6">
          <h2 className="mb-4 text-lg font-semibold text-white">
            {editingId ? "Edit Recipient" : "New Recipient"}
          </h2>
          {formError && (
            <div className="mb-4 rounded-lg border border-red-500/30 bg-red-500/10 px-4 py-3 text-sm text-red-400">
              {formError}
            </div>
          )}
          <form onSubmit={handleSubmit} className="grid gap-4 sm:grid-cols-2">
            <div>
              <label className="mb-1 block text-sm font-medium text-slate-300">
                Full Name *
              </label>
              <input
                type="text"
                value={fullName}
                onChange={(e) => setFullName(e.target.value)}
                className="w-full rounded-lg border border-slate-600 bg-slate-700/50 px-4 py-2.5 text-white placeholder-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                required
              />
            </div>
            <div>
              <label className="mb-1 block text-sm font-medium text-slate-300">
                Email *
              </label>
              <input
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                className="w-full rounded-lg border border-slate-600 bg-slate-700/50 px-4 py-2.5 text-white placeholder-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                required
              />
            </div>
            <div>
              <label className="mb-1 block text-sm font-medium text-slate-300">
                Phone
              </label>
              <input
                type="tel"
                value={phone}
                onChange={(e) => setPhone(e.target.value)}
                className="w-full rounded-lg border border-slate-600 bg-slate-700/50 px-4 py-2.5 text-white placeholder-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              />
            </div>
            <div>
              <label className="mb-1 block text-sm font-medium text-slate-300">
                Relationship *
              </label>
              <input
                type="text"
                value={relationship}
                onChange={(e) => setRelationship(e.target.value)}
                className="w-full rounded-lg border border-slate-600 bg-slate-700/50 px-4 py-2.5 text-white placeholder-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                placeholder="e.g. Spouse, Child, Friend"
                required
              />
            </div>
            <div className="sm:col-span-2 flex gap-3">
              <button
                type="submit"
                className="rounded-lg bg-indigo-600 px-6 py-2.5 text-sm font-semibold text-white hover:bg-indigo-500 transition-colors"
              >
                {editingId ? "Update" : "Create"}
              </button>
              <button
                type="button"
                onClick={resetForm}
                className="rounded-lg bg-slate-700 px-6 py-2.5 text-sm font-medium text-slate-300 hover:bg-slate-600 transition-colors"
              >
                Cancel
              </button>
            </div>
          </form>
        </div>
      )}

      {/* List */}
      {loading ? (
        <div className="py-12 text-center text-slate-400">Loading…</div>
      ) : recipients.length === 0 ? (
        <div className="rounded-xl border border-slate-700/50 bg-slate-800/50 py-16 text-center">
          <p className="text-lg text-slate-400">No recipients yet.</p>
          <p className="text-sm text-slate-500">
            Add someone who should receive your vault items.
          </p>
        </div>
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {recipients.map((r) => (
            <div
              key={r.id}
              className="rounded-xl border border-slate-700/50 bg-slate-800/50 p-5"
            >
              <div className="mb-3 flex items-start justify-between">
                <div>
                  <h3 className="font-semibold text-white">{r.fullName}</h3>
                  <p className="text-sm text-slate-400">{r.relationship}</p>
                </div>
                {r.isEmailVerified && (
                  <span className="rounded-full bg-emerald-600/20 px-2.5 py-0.5 text-xs font-medium text-emerald-400">
                    Verified
                  </span>
                )}
              </div>
              <p className="text-sm text-slate-300">{r.email}</p>
              {r.phone && (
                <p className="text-sm text-slate-400">{r.phone}</p>
              )}
              <div className="mt-4 flex gap-2">
                <button
                  onClick={() => startEdit(r)}
                  className="rounded-md bg-slate-700 px-3 py-1.5 text-xs font-medium text-slate-300 hover:bg-slate-600 transition-colors"
                >
                  Edit
                </button>
                <button
                  onClick={() => handleDelete(r.id)}
                  className="rounded-md bg-red-600/20 px-3 py-1.5 text-xs font-medium text-red-400 hover:bg-red-600/30 transition-colors"
                >
                  Delete
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export default function RecipientsPage() {
  return (
    <ProtectedRoute>
      <RecipientsContent />
    </ProtectedRoute>
  );
}
