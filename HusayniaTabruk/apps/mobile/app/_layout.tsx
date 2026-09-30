import { Stack } from "expo-router";

import { AuthSessionProvider } from "../src/features/auth/AuthSessionProvider";
import { SignupRecoveryRuntime } from "../src/features/signups/SignupRecoveryRuntime";

export default function RootLayout() {
  return (
    <AuthSessionProvider>
      <SignupRecoveryRuntime />
      <Stack
        screenOptions={{
          headerTitle: "Tabruk",
        }}
      >
        <Stack.Screen name="(auth)" options={{ headerShown: false }} />
        <Stack.Screen name="(member)" options={{ headerShown: false }} />
        <Stack.Screen name="(incharge)" options={{ headerShown: false }} />
      </Stack>
    </AuthSessionProvider>
  );
}
