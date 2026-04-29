"use client";

import type { ReactNode } from "react";
import { AuthProvider } from "@/lib/auth-context";
import Navbar from "@/components/Navbar";

export default function ClientLayout({ children }: { children: ReactNode }) {
  return (
    <AuthProvider>
      <Navbar />
      <main className="flex flex-1 flex-col">{children}</main>
    </AuthProvider>
  );
}
