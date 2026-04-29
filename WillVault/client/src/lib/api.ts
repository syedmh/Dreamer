import type {
  AuthResponse,
  LoginRequest,
  PagedResult,
  Recipient,
  RegisterRequest,
  TrustedContact,
  VaultItem,
  VaultItemDetail,
} from "./types";

const BASE_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5273";

function getAccessToken(): string | null {
  if (typeof window === "undefined") return null;
  return localStorage.getItem("accessToken");
}

function getRefreshToken(): string | null {
  if (typeof window === "undefined") return null;
  return localStorage.getItem("refreshToken");
}

function setTokens(access: string, refresh: string): void {
  localStorage.setItem("accessToken", access);
  localStorage.setItem("refreshToken", refresh);
}

function clearTokens(): void {
  localStorage.removeItem("accessToken");
  localStorage.removeItem("refreshToken");
  localStorage.removeItem("user");
}

async function refreshAccessToken(): Promise<string | null> {
  const refresh = getRefreshToken();
  if (!refresh) return null;

  try {
    const res = await fetch(`${BASE_URL}/api/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: refresh }),
    });

    if (!res.ok) {
      clearTokens();
      return null;
    }

    const data: AuthResponse = await res.json();
    setTokens(data.accessToken, data.refreshToken);
    localStorage.setItem("user", JSON.stringify(data.user));
    return data.accessToken;
  } catch {
    clearTokens();
    return null;
  }
}

async function apiFetch<T>(
  path: string,
  options: RequestInit = {}
): Promise<T> {
  const headers: Record<string, string> = {
    ...(options.headers as Record<string, string>),
  };

  const token = getAccessToken();
  if (token) {
    headers["Authorization"] = `Bearer ${token}`;
  }

  // Don't set Content-Type for FormData (browser sets it with boundary)
  if (!(options.body instanceof FormData) && !headers["Content-Type"]) {
    headers["Content-Type"] = "application/json";
  }

  let res = await fetch(`${BASE_URL}${path}`, { ...options, headers });

  // Auto-refresh on 401
  if (res.status === 401 && token) {
    const newToken = await refreshAccessToken();
    if (newToken) {
      headers["Authorization"] = `Bearer ${newToken}`;
      res = await fetch(`${BASE_URL}${path}`, { ...options, headers });
    }
  }

  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(text || `Request failed with status ${res.status}`);
  }

  // Handle 204 No Content
  if (res.status === 204) return undefined as T;

  return res.json();
}

// ── Auth ──────────────────────────────────────────────────────────

export async function register(
  req: RegisterRequest
): Promise<AuthResponse> {
  return apiFetch<AuthResponse>("/api/auth/register", {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export async function login(req: LoginRequest): Promise<AuthResponse> {
  return apiFetch<AuthResponse>("/api/auth/login", {
    method: "POST",
    body: JSON.stringify(req),
  });
}

export function logout(): void {
  clearTokens();
}

// ── Vault ─────────────────────────────────────────────────────────

export async function getVaultItems(): Promise<VaultItem[]> {
  return apiFetch<VaultItem[]>("/api/vault");
}

export async function getVaultItem(id: string): Promise<VaultItemDetail> {
  return apiFetch<VaultItemDetail>(`/api/vault/${id}`);
}

export async function createVaultItem(
  data: Partial<VaultItem>
): Promise<VaultItem> {
  return apiFetch<VaultItem>("/api/vault", {
    method: "POST",
    body: JSON.stringify(data),
  });
}

export async function uploadVaultItem(form: FormData): Promise<VaultItem> {
  return apiFetch<VaultItem>("/api/vault/upload", {
    method: "POST",
    body: form,
  });
}

export async function updateVaultItem(
  id: string,
  data: Partial<VaultItem>
): Promise<VaultItem> {
  return apiFetch<VaultItem>(`/api/vault/${id}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
}

export async function deleteVaultItem(id: string): Promise<void> {
  return apiFetch<void>(`/api/vault/${id}`, { method: "DELETE" });
}

export async function assignRecipient(
  vaultId: string,
  recipientId: string
): Promise<void> {
  return apiFetch<void>(`/api/vault/${vaultId}/recipients`, {
    method: "POST",
    body: JSON.stringify({ recipientId }),
  });
}

export async function removeRecipient(
  vaultId: string,
  recipientId: string
): Promise<void> {
  return apiFetch<void>(
    `/api/vault/${vaultId}/recipients/${recipientId}`,
    { method: "DELETE" }
  );
}

// ── Recipients ────────────────────────────────────────────────────

export async function getRecipients(): Promise<Recipient[]> {
  return apiFetch<Recipient[]>("/api/recipients");
}

export async function createRecipient(
  data: Partial<Recipient>
): Promise<Recipient> {
  return apiFetch<Recipient>("/api/recipients", {
    method: "POST",
    body: JSON.stringify(data),
  });
}

export async function updateRecipient(
  id: string,
  data: Partial<Recipient>
): Promise<Recipient> {
  return apiFetch<Recipient>(`/api/recipients/${id}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
}

export async function deleteRecipient(id: string): Promise<void> {
  return apiFetch<void>(`/api/recipients/${id}`, { method: "DELETE" });
}

// ── Trusted Contacts ──────────────────────────────────────────────

export async function getTrustedContacts(): Promise<TrustedContact[]> {
  return apiFetch<TrustedContact[]>("/api/trusted-contacts");
}

export async function createTrustedContact(
  data: Partial<TrustedContact>
): Promise<TrustedContact> {
  return apiFetch<TrustedContact>("/api/trusted-contacts", {
    method: "POST",
    body: JSON.stringify(data),
  });
}

export async function updateTrustedContact(
  id: string,
  data: Partial<TrustedContact>
): Promise<TrustedContact> {
  return apiFetch<TrustedContact>(`/api/trusted-contacts/${id}`, {
    method: "PUT",
    body: JSON.stringify(data),
  });
}

export async function deleteTrustedContact(id: string): Promise<void> {
  return apiFetch<void>(`/api/trusted-contacts/${id}`, {
    method: "DELETE",
  });
}
