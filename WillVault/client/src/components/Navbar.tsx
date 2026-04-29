"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useState } from "react";
import { useAuth } from "@/lib/auth-context";

const authedLinks = [
  { href: "/dashboard", label: "Dashboard" },
  { href: "/vault", label: "Vault" },
  { href: "/recipients", label: "Recipients" },
  { href: "/trusted-contacts", label: "Trusted Contacts" },
];

export default function Navbar() {
  const { user, logout } = useAuth();
  const pathname = usePathname();
  const [menuOpen, setMenuOpen] = useState(false);

  const isActive = (href: string) =>
    pathname === href || pathname.startsWith(href + "/");

  return (
    <nav className="bg-slate-900 border-b border-slate-700/50">
      <div className="mx-auto max-w-7xl px-4 sm:px-6 lg:px-8">
        <div className="flex h-16 items-center justify-between">
          {/* Logo */}
          <Link
            href="/"
            className="text-xl font-bold text-indigo-400 tracking-tight"
          >
            WillVault
          </Link>

          {/* Desktop links */}
          <div className="hidden md:flex md:items-center md:gap-1">
            {user ? (
              <>
                {authedLinks.map((link) => (
                  <Link
                    key={link.href}
                    href={link.href}
                    className={`rounded-md px-3 py-2 text-sm font-medium transition-colors ${
                      isActive(link.href)
                        ? "bg-indigo-600/20 text-indigo-300"
                        : "text-slate-300 hover:bg-slate-800 hover:text-white"
                    }`}
                  >
                    {link.label}
                  </Link>
                ))}
                <div className="ml-4 flex items-center gap-3 border-l border-slate-700 pl-4">
                  <span className="text-sm text-slate-400">
                    {user.fullName}
                  </span>
                  <button
                    onClick={logout}
                    className="rounded-md bg-slate-800 px-3 py-1.5 text-sm font-medium text-slate-300 hover:bg-slate-700 hover:text-white transition-colors"
                  >
                    Logout
                  </button>
                </div>
              </>
            ) : (
              <>
                <Link
                  href="/login"
                  className={`rounded-md px-3 py-2 text-sm font-medium transition-colors ${
                    isActive("/login")
                      ? "bg-indigo-600/20 text-indigo-300"
                      : "text-slate-300 hover:bg-slate-800 hover:text-white"
                  }`}
                >
                  Login
                </Link>
                <Link
                  href="/register"
                  className="rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500 transition-colors"
                >
                  Register
                </Link>
              </>
            )}
          </div>

          {/* Mobile menu button */}
          <button
            className="md:hidden rounded-md p-2 text-slate-400 hover:bg-slate-800 hover:text-white"
            onClick={() => setMenuOpen(!menuOpen)}
            aria-label="Toggle menu"
          >
            <svg
              className="h-6 w-6"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
            >
              {menuOpen ? (
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M6 18L18 6M6 6l12 12"
                />
              ) : (
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M3.75 6.75h16.5M3.75 12h16.5m-16.5 5.25h16.5"
                />
              )}
            </svg>
          </button>
        </div>
      </div>

      {/* Mobile menu */}
      {menuOpen && (
        <div className="md:hidden border-t border-slate-700/50 bg-slate-900 px-4 pb-4 pt-2">
          {user ? (
            <>
              <div className="mb-3 border-b border-slate-700/50 pb-3">
                <p className="text-sm text-slate-400">{user.fullName}</p>
              </div>
              {authedLinks.map((link) => (
                <Link
                  key={link.href}
                  href={link.href}
                  onClick={() => setMenuOpen(false)}
                  className={`block rounded-md px-3 py-2 text-sm font-medium ${
                    isActive(link.href)
                      ? "bg-indigo-600/20 text-indigo-300"
                      : "text-slate-300 hover:bg-slate-800"
                  }`}
                >
                  {link.label}
                </Link>
              ))}
              <button
                onClick={() => {
                  logout();
                  setMenuOpen(false);
                }}
                className="mt-2 w-full rounded-md bg-slate-800 px-3 py-2 text-left text-sm font-medium text-slate-300 hover:bg-slate-700"
              >
                Logout
              </button>
            </>
          ) : (
            <>
              <Link
                href="/login"
                onClick={() => setMenuOpen(false)}
                className="block rounded-md px-3 py-2 text-sm font-medium text-slate-300 hover:bg-slate-800"
              >
                Login
              </Link>
              <Link
                href="/register"
                onClick={() => setMenuOpen(false)}
                className="block rounded-md px-3 py-2 text-sm font-medium text-indigo-400 hover:bg-slate-800"
              >
                Register
              </Link>
            </>
          )}
        </div>
      )}
    </nav>
  );
}
