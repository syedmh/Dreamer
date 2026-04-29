"use client";

import { useRouter } from "next/navigation";
import { useRef, useState } from "react";
import ProtectedRoute from "@/components/ProtectedRoute";
import * as api from "@/lib/api";
import { VaultItemType, VaultItemTypeLabels } from "@/lib/types";

function CreateContent() {
  const router = useRouter();
  const fileInputRef = useRef<HTMLInputElement>(null);

  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [content, setContent] = useState("");
  const [itemType, setItemType] = useState<VaultItemType>(
    VaultItemType.TextNote
  );
  const [file, setFile] = useState<File | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const [dragActive, setDragActive] = useState(false);

  const isFileType =
    itemType === VaultItemType.VoiceNote ||
    itemType === VaultItemType.Video ||
    itemType === VaultItemType.Document;

  const handleDrag = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setDragActive(e.type === "dragenter" || e.type === "dragover");
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setDragActive(false);
    if (e.dataTransfer.files?.[0]) {
      setFile(e.dataTransfer.files[0]);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");

    if (!title.trim()) {
      setError("Title is required.");
      return;
    }

    setLoading(true);
    try {
      if (isFileType && file) {
        const form = new FormData();
        form.append("title", title);
        form.append("description", description);
        form.append("itemType", String(itemType));
        form.append("file", file);
        await api.uploadVaultItem(form);
      } else {
        await api.createVaultItem({
          title,
          description,
          itemType,
        });
      }
      router.push("/vault");
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Failed to create item."
      );
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="mx-auto max-w-2xl px-4 py-8 sm:px-6">
      <h1 className="mb-8 text-2xl font-bold text-white">
        Create Vault Item
      </h1>

      {error && (
        <div className="mb-4 rounded-lg border border-red-500/30 bg-red-500/10 px-4 py-3 text-sm text-red-400">
          {error}
        </div>
      )}

      <form
        onSubmit={handleSubmit}
        className="space-y-6 rounded-xl border border-slate-700/50 bg-slate-800/50 p-6"
      >
        {/* Type selector */}
        <div>
          <label className="mb-1.5 block text-sm font-medium text-slate-300">
            Item Type
          </label>
          <div className="flex flex-wrap gap-2">
            {(
              Object.values(VaultItemType).filter(
                (v) => typeof v === "number"
              ) as VaultItemType[]
            ).map((t) => (
              <button
                key={t}
                type="button"
                onClick={() => {
                  setItemType(t);
                  setFile(null);
                }}
                className={`rounded-lg px-4 py-2 text-sm font-medium transition-colors ${
                  itemType === t
                    ? "bg-indigo-600 text-white"
                    : "bg-slate-700 text-slate-300 hover:bg-slate-600"
                }`}
              >
                {VaultItemTypeLabels[t]}
              </button>
            ))}
          </div>
        </div>

        {/* Title */}
        <div>
          <label
            htmlFor="title"
            className="mb-1.5 block text-sm font-medium text-slate-300"
          >
            Title <span className="text-red-400">*</span>
          </label>
          <input
            id="title"
            type="text"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            className="w-full rounded-lg border border-slate-600 bg-slate-700/50 px-4 py-2.5 text-white placeholder-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
            placeholder="Give your item a title"
            required
          />
        </div>

        {/* Description */}
        <div>
          <label
            htmlFor="description"
            className="mb-1.5 block text-sm font-medium text-slate-300"
          >
            Description
          </label>
          <textarea
            id="description"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            rows={3}
            className="w-full rounded-lg border border-slate-600 bg-slate-700/50 px-4 py-2.5 text-white placeholder-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
            placeholder="Optional description"
          />
        </div>

        {/* Content for text notes */}
        {itemType === VaultItemType.TextNote && (
          <div>
            <label
              htmlFor="content"
              className="mb-1.5 block text-sm font-medium text-slate-300"
            >
              Content
            </label>
            <textarea
              id="content"
              value={content}
              onChange={(e) => setContent(e.target.value)}
              rows={8}
              className="w-full rounded-lg border border-slate-600 bg-slate-700/50 px-4 py-2.5 text-white placeholder-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500 font-mono text-sm"
              placeholder="Write your note here…"
            />
          </div>
        )}

        {/* File upload for file types */}
        {isFileType && (
          <div>
            <label className="mb-1.5 block text-sm font-medium text-slate-300">
              File Upload
            </label>
            <div
              onDragEnter={handleDrag}
              onDragLeave={handleDrag}
              onDragOver={handleDrag}
              onDrop={handleDrop}
              onClick={() => fileInputRef.current?.click()}
              className={`cursor-pointer rounded-lg border-2 border-dashed p-8 text-center transition-colors ${
                dragActive
                  ? "border-indigo-500 bg-indigo-500/10"
                  : "border-slate-600 hover:border-slate-500 bg-slate-700/30"
              }`}
            >
              {file ? (
                <div>
                  <p className="font-medium text-white">{file.name}</p>
                  <p className="text-sm text-slate-400">
                    {(file.size / 1024 / 1024).toFixed(2)} MB
                  </p>
                </div>
              ) : (
                <div>
                  <p className="mb-1 text-slate-300">
                    Drag & drop your file here
                  </p>
                  <p className="text-sm text-slate-500">
                    or click to browse
                  </p>
                </div>
              )}
              <input
                ref={fileInputRef}
                type="file"
                className="hidden"
                onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              />
            </div>
          </div>
        )}

        <div className="flex gap-3">
          <button
            type="submit"
            disabled={loading}
            className="rounded-lg bg-indigo-600 px-6 py-2.5 text-sm font-semibold text-white hover:bg-indigo-500 disabled:opacity-50 transition-colors"
          >
            {loading ? "Creating…" : "Create Item"}
          </button>
          <button
            type="button"
            onClick={() => router.push("/vault")}
            className="rounded-lg bg-slate-700 px-6 py-2.5 text-sm font-medium text-slate-300 hover:bg-slate-600 transition-colors"
          >
            Cancel
          </button>
        </div>
      </form>
    </div>
  );
}

export default function CreateVaultItemPage() {
  return (
    <ProtectedRoute>
      <CreateContent />
    </ProtectedRoute>
  );
}
