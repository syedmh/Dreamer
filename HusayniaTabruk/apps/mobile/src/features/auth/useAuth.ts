import { useContext } from "react";

import { AuthContext, type AuthContextValue } from "./AuthSessionProvider";

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);

  if (!context) {
    throw new Error("useAuth must be used within an AuthSessionProvider.");
  }

  return context;
}
