import { Redirect, Stack } from "expo-router";

import { AuthRestoringScreen } from "../../src/features/auth/AuthLoadingScreen";
import { useAuth } from "../../src/features/auth/useAuth";

export default function MemberLayout() {
  const { status } = useAuth();

  if (status === "restoring") {
    return <AuthRestoringScreen />;
  }

  if (status !== "authenticated") {
    return <Redirect href="/(auth)" />;
  }

  return <Stack screenOptions={{ headerShown: false }} />;
}
