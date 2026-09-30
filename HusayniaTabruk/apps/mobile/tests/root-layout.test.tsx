import { describe, expect, it } from "@jest/globals";
import { Stack } from "expo-router";

import RootLayout from "../app/_layout";
import { AuthSessionProvider } from "../src/features/auth/AuthSessionProvider";
import { SignupRecoveryRuntime } from "../src/features/signups/SignupRecoveryRuntime";

describe("RootLayout", () => {
  it("exports the Tabruk navigation shell with its auth provider and expected title", () => {
    const shell = RootLayout();
    const [recoveryRuntime, stack] = shell.props.children;

    expect(shell.type).toBe(AuthSessionProvider);
    expect(recoveryRuntime.type).toBe(SignupRecoveryRuntime);
    expect(stack.type).toBe(Stack);
    expect(stack.props.screenOptions).toEqual({
      headerTitle: "Tabruk",
    });
    expect(stack.props.children.map((screen: { props: { name: string } }) => screen.props.name)).toEqual(
      ["(auth)", "(member)", "(incharge)"],
    );
  });
});
