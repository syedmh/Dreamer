import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import type { ReactNode } from "react";
import { TextInput } from "react-native";
import { act, create, type ReactTestRenderer } from "react-test-renderer";

import { ApiClientError, type ProblemDetails } from "../../../src/core/api/auth-api";
import { AuthSessionProvider } from "../../../src/features/auth/AuthSessionProvider";
import { LoginScreen } from "../../../src/features/auth/LoginScreen";
import type { AuthSessionCoordinator } from "../../../src/core/security/auth-session-controller";
import type { AuthSession } from "../../../src/core/security/auth-session-types";

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
  installationId: "bba66bbd-aab4-4f42-b970-a576ed676a80",
  refreshToken: "refresh-token-value",
  refreshTokenExpiresAt: "2026-09-16T12:00:00.000Z",
};

describe("LoginScreen", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("renders the exact API auth failure in an accessible alert", async () => {
    const controller = createControllerDouble({
      signIn: jest.fn<AuthSessionCoordinator["signIn"]>().mockRejectedValue(
        createProblemError(401, "authentication_failed", "Authentication failed."),
      ),
    });

    const renderer = await renderWithProvider(controller, <LoginScreen />);

    await submitCredentials(renderer);

    const alert = renderer.root.findByProps({ accessibilityRole: "alert" });
    expect(readText(alert)).toContain("Authentication failed.");

    const passwordInput = renderer.root.findByProps({
      accessibilityLabel: "Password",
    });
    expect(passwordInput.props.secureTextEntry).toBe(true);
  });

  it("surfaces failed-refresh logout messaging after restore returns to the auth shell", async () => {
    const controller = createControllerDouble({
      restore: jest.fn<AuthSessionCoordinator["restore"]>().mockRejectedValue(
        createProblemError(
          401,
          "refresh_token_reused",
          "The refresh token was already used and the session has been revoked.",
        ),
      ),
    });

    const renderer = await renderWithProvider(controller, <LoginScreen />);

    const alert = renderer.root.findByProps({ accessibilityRole: "alert" });
    expect(readText(alert)).toContain(
      "The refresh token was already used and the session has been revoked.",
    );
  });
});

async function renderWithProvider(
  controller: AuthSessionCoordinator,
  element: ReactNode,
): Promise<ReactTestRenderer> {
  let renderer: ReactTestRenderer | undefined;

  await act(async () => {
    renderer = create(
      <AuthSessionProvider controller={controller}>{element}</AuthSessionProvider>,
    );
  });

  await flush();

  if (!renderer) {
    throw new Error("Renderer was not created.");
  }

  return renderer;
}

async function submitCredentials(renderer: ReactTestRenderer): Promise<void> {
  const inputs = renderer.root.findAllByType(TextInput);
  const emailInput = inputs.find(
    (candidate) => candidate.props.accessibilityLabel === "Email address",
  );
  const passwordInput = inputs.find(
    (candidate) => candidate.props.accessibilityLabel === "Password",
  );
  const button = renderer.root.findByProps({
    accessibilityLabel: "Sign in securely",
  });

  await act(async () => {
    emailInput?.props.onChangeText("member@example.test");
    passwordInput?.props.onChangeText("WrongPassw0rd!");
  });

  await flush();

  await act(async () => {
    button.props.onPress();
  });

  await flush();
}

function createControllerDouble(
  overrides: Partial<{
    restore: jest.MockedFunction<AuthSessionCoordinator["restore"]>;
    signIn: jest.MockedFunction<AuthSessionCoordinator["signIn"]>;
    signOut: jest.MockedFunction<AuthSessionCoordinator["signOut"]>;
    synchronize: jest.MockedFunction<AuthSessionCoordinator["synchronize"]>;
  }> = {},
): AuthSessionCoordinator {
  return {
    restore: overrides.restore ?? jest.fn<AuthSessionCoordinator["restore"]>().mockResolvedValue(null),
    signIn:
      overrides.signIn ??
      jest.fn<AuthSessionCoordinator["signIn"]>().mockResolvedValue(sampleSession),
    signOut:
      overrides.signOut ??
      jest.fn<AuthSessionCoordinator["signOut"]>().mockResolvedValue(undefined),
    synchronize:
      overrides.synchronize ??
      jest
        .fn<AuthSessionCoordinator["synchronize"]>()
        .mockResolvedValue(sampleSession),
  };
}

function createProblemError(
  status: number,
  code: string,
  detail: string,
): ApiClientError {
  const problem: ProblemDetails = {
    code,
    detail,
    status,
    title: "Problem",
    traceId: "trace-id",
    type: `https://httpstatuses.com/${status}`,
  };

  return new ApiClientError({
    problem,
    status,
  });
}

async function flush(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
  });
}

function readText(node: { children?: readonly unknown[] }): string {
  return (node.children ?? [])
    .flatMap((child) => {
      if (typeof child === "string") {
        return [child];
      }

      if (typeof child === "object" && child !== null && "props" in child) {
        return [readText(child as { children?: readonly unknown[]; props?: unknown })];
      }

      return [];
    })
    .join(" ");
}
