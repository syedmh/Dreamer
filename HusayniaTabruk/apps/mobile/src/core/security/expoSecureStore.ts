import { requireOptionalNativeModule } from "expo-modules-core";

export type KeychainAccessibilityConstant = number;

export interface SecureStoreOptions {
  readonly accessGroup?: string;
  readonly authenticationPrompt?: string;
  readonly keychainAccessible?: KeychainAccessibilityConstant;
  readonly keychainService?: string;
  readonly requireAuthentication?: boolean;
}

type ExpoSecureStoreNativeModule = {
  readonly AFTER_FIRST_UNLOCK: KeychainAccessibilityConstant;
  readonly AFTER_FIRST_UNLOCK_THIS_DEVICE_ONLY: KeychainAccessibilityConstant;
  readonly ALWAYS: KeychainAccessibilityConstant;
  readonly ALWAYS_THIS_DEVICE_ONLY: KeychainAccessibilityConstant;
  readonly WHEN_PASSCODE_SET_THIS_DEVICE_ONLY: KeychainAccessibilityConstant;
  readonly WHEN_UNLOCKED: KeychainAccessibilityConstant;
  readonly WHEN_UNLOCKED_THIS_DEVICE_ONLY: KeychainAccessibilityConstant;
  deleteValueWithKeyAsync(key: string, options?: SecureStoreOptions): Promise<void>;
  getValueWithKeyAsync(key: string, options?: SecureStoreOptions): Promise<string | null>;
  setValueWithKeyAsync(value: string, key: string, options?: SecureStoreOptions): Promise<void>;
};

const ExpoSecureStore =
  requireOptionalNativeModule<ExpoSecureStoreNativeModule>("ExpoSecureStore");

export const WHEN_UNLOCKED_THIS_DEVICE_ONLY: KeychainAccessibilityConstant =
  ExpoSecureStore?.WHEN_UNLOCKED_THIS_DEVICE_ONLY ?? 6;

export async function deleteItemAsync(
  key: string,
  options: SecureStoreOptions = {},
): Promise<void> {
  const secureStore = getSecureStoreModule();
  ensureValidKey(key);

  await secureStore.deleteValueWithKeyAsync(key, options);
}

export async function getItemAsync(
  key: string,
  options: SecureStoreOptions = {},
): Promise<string | null> {
  const secureStore = getSecureStoreModule();
  ensureValidKey(key);

  return secureStore.getValueWithKeyAsync(key, options);
}

export async function isAvailableAsync(): Promise<boolean> {
  return typeof ExpoSecureStore?.getValueWithKeyAsync === "function";
}

export async function setItemAsync(
  key: string,
  value: string,
  options: SecureStoreOptions = {},
): Promise<void> {
  const secureStore = getSecureStoreModule();
  ensureValidKey(key);
  ensureValidValue(value);

  await secureStore.setValueWithKeyAsync(value, key, options);
}

function getSecureStoreModule(): ExpoSecureStoreNativeModule {
  if (!ExpoSecureStore) {
    throw new Error("Expo SecureStore is unavailable on this device.");
  }

  return ExpoSecureStore;
}

function ensureValidKey(key: string): void {
  if (typeof key !== "string" || !/^[\w.-]+$/.test(key)) {
    throw new Error(
      'Invalid key provided to SecureStore. Keys must contain only letters, numbers, ".", "-", and "_".',
    );
  }
}

function ensureValidValue(value: string): void {
  if (typeof value !== "string") {
    throw new Error(
      "Invalid value provided to SecureStore. Values must be strings; JSON-encode structured data first.",
    );
  }
}
