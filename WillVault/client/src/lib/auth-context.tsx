"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from "react";
import type { ReactNode } from "react";
import * as api from "./api";
import type { LoginRequest, RegisterRequest, User } from "./types";

interface AuthState {
  user: User | null;
  accessToken: string | null;
  isLoading: boolean;
  login: (req: LoginRequest) => Promise<void>;
  register: (req: RegisterRequest) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthState | undefined>(undefined);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [accessToken, setAccessToken] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // Load persisted auth on mount
  useEffect(() => {
    try {
      const storedToken = localStorage.getItem("accessToken");
      const storedUser = localStorage.getItem("user");
      if (storedToken && storedUser) {
        setAccessToken(storedToken);
        setUser(JSON.parse(storedUser));
      }
    } catch {
      // Ignore corrupt localStorage
    } finally {
      setIsLoading(false);
    }
  }, []);

  const handleLogin = useCallback(async (req: LoginRequest) => {
    const res = await api.login(req);
    localStorage.setItem("accessToken", res.accessToken);
    localStorage.setItem("refreshToken", res.refreshToken);
    localStorage.setItem("user", JSON.stringify(res.user));
    setAccessToken(res.accessToken);
    setUser(res.user);
  }, []);

  const handleRegister = useCallback(async (req: RegisterRequest) => {
    const res = await api.register(req);
    localStorage.setItem("accessToken", res.accessToken);
    localStorage.setItem("refreshToken", res.refreshToken);
    localStorage.setItem("user", JSON.stringify(res.user));
    setAccessToken(res.accessToken);
    setUser(res.user);
  }, []);

  const handleLogout = useCallback(() => {
    api.logout();
    setAccessToken(null);
    setUser(null);
  }, []);

  const value = useMemo(
    () => ({
      user,
      accessToken,
      isLoading,
      login: handleLogin,
      register: handleRegister,
      logout: handleLogout,
    }),
    [user, accessToken, isLoading, handleLogin, handleRegister, handleLogout]
  );

  return (
    <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
  );
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error("useAuth must be used within an AuthProvider");
  }
  return ctx;
}
