import type { AuthSession } from "./auth-session-types";
import {
  WHEN_UNLOCKED_THIS_DEVICE_ONLY,
  deleteItemAsync,
  getItemAsync,
  setItemAsync,
  type SecureStoreOptions,
} from "./expoSecureStore";

const installationKey = "tabruk.auth.installation";
const sessionKey = "tabruk.auth.session";

const secureStoreOptions: SecureStoreOptions = {
  keychainAccessible: WHEN_UNLOCKED_THIS_DEVICE_ONLY,
};

export interface AuthStorage {
  clearSession(): Promise<void>;
  loadInstallationId(): Promise<string | null>;
  loadSession(): Promise<AuthSession | null>;
  saveInstallationId(installationId: string): Promise<void>;
  saveSession(session: AuthSession): Promise<void>;
}

export class SecureStoreAuthStorage implements AuthStorage {
  public async clearSession(): Promise<void> {
    await deleteItemAsync(sessionKey, secureStoreOptions);
  }

  public async loadInstallationId(): Promise<string | null> {
    const installationId = await getItemAsync(installationKey, secureStoreOptions);
    return typeof installationId === "string" && installationId.length > 0 ? installationId : null;
  }

  public async loadSession(): Promise<AuthSession | null> {
    const rawSession = await getItemAsync(sessionKey, secureStoreOptions);

    if (!rawSession) {
      return null;
    }

    try {
      const parsed = JSON.parse(rawSession) as unknown;
      if (isAuthSession(parsed)) {
        return parsed;
      }
    } catch {
      // Corrupted secure storage should be purged without logging secrets.
    }

    await this.clearSession();
    return null;
  }

  public async saveInstallationId(installationId: string): Promise<void> {
    await setItemAsync(installationKey, installationId, secureStoreOptions);
  }

  public async saveSession(session: AuthSession): Promise<void> {
    await setItemAsync(sessionKey, JSON.stringify(session), secureStoreOptions);
  }
}

function isAuthSession(value: unknown): value is AuthSession {
  if (!isRecord(value)) {
    return false;
  }

  return (
    isNonEmptyString(value.installationId) &&
    isNonEmptyString(value.accessToken) &&
    isNonEmptyString(value.accessTokenExpiresAt) &&
    isNonEmptyString(value.refreshToken) &&
    isNonEmptyString(value.refreshTokenExpiresAt) &&
    isMeResponse(value.actor)
  );
}

function isMeResponse(value: unknown): boolean {
  if (!isRecord(value)) {
    return false;
  }

  return (
    isMembership(value.membership) &&
    Array.isArray(value.roles) &&
    value.roles.every(isNonEmptyString) &&
    isOrganization(value.organization)
  );
}

function isMembership(value: unknown): boolean {
  return (
    isRecord(value) &&
    isNonEmptyString(value.id) &&
    isNonEmptyString(value.displayName) &&
    typeof value.eligibleAsNamedParticipant === "boolean"
  );
}

function isOrganization(value: unknown): boolean {
  return (
    isRecord(value) &&
    isNonEmptyString(value.id) &&
    isNonEmptyString(value.name) &&
    isNonEmptyString(value.timeZone)
  );
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
