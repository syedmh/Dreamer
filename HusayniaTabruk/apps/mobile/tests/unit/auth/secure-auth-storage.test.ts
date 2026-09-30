import { beforeEach, describe, expect, it, jest } from "@jest/globals";

import * as secureStoreModule from "../../../src/core/security/expoSecureStore";
import { SecureStoreAuthStorage } from "../../../src/core/security/secure-auth-storage";
import type { AuthSession } from "../../../src/core/security/auth-session-types";

jest.mock("../../../src/core/security/expoSecureStore", () => ({
  WHEN_UNLOCKED_THIS_DEVICE_ONLY: 6,
  deleteItemAsync: jest.fn(),
  getItemAsync: jest.fn(),
  setItemAsync: jest.fn(),
}));

jest.mock("@react-native-async-storage/async-storage", () => ({
  getItem: jest.fn(),
  removeItem: jest.fn(),
  setItem: jest.fn(),
}), {
  virtual: true,
});

const mockSecureStore = secureStoreModule as jest.Mocked<typeof secureStoreModule>;
const mockAsyncStorage = jest.requireMock("@react-native-async-storage/async-storage") as {
  getItem: jest.Mock;
  removeItem: jest.Mock;
  setItem: jest.Mock;
};

const sampleSession: AuthSession = {
  accessToken: "access-token-value",
  accessTokenExpiresAt: "2026-08-17T12:00:00.000Z",
  actor: {
    membership: {
      displayName: "Test Member",
      eligibleAsNamedParticipant: true,
      id: "membership-1",
    },
    organization: {
      id: "organization-1",
      name: "Husaynia",
      timeZone: "America/Los_Angeles",
    },
    roles: ["Member"],
  },
  installationId: "7fe36817-6f6b-41bd-87f1-5d6ef433775b",
  refreshToken: "refresh-token-value",
  refreshTokenExpiresAt: "2026-09-16T12:00:00.000Z",
};

describe("SecureStoreAuthStorage", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("persists auth session JSON only through Expo SecureStore", async () => {
    const storage = new SecureStoreAuthStorage();

    await storage.saveSession(sampleSession);

    expect(mockSecureStore.setItemAsync).toHaveBeenCalledTimes(1);
    expect(mockSecureStore.setItemAsync).toHaveBeenCalledWith(
      "tabruk.auth.session",
      JSON.stringify(sampleSession),
      { keychainAccessible: 6 },
    );
    expect(mockAsyncStorage.setItem).not.toHaveBeenCalled();
    expect(mockAsyncStorage.getItem).not.toHaveBeenCalled();
    expect(mockAsyncStorage.removeItem).not.toHaveBeenCalled();
  });

  it("purges malformed secure session payloads without leaking them elsewhere", async () => {
    const storage = new SecureStoreAuthStorage();
    mockSecureStore.getItemAsync.mockResolvedValueOnce("{not-valid-json");

    const restored = await storage.loadSession();

    expect(restored).toBeNull();
    expect(mockSecureStore.deleteItemAsync).toHaveBeenCalledWith(
      "tabruk.auth.session",
      { keychainAccessible: 6 },
    );
    expect(mockAsyncStorage.removeItem).not.toHaveBeenCalled();
  });
});
