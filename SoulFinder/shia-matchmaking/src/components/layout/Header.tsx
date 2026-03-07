"use client";

import Link from "next/link";
import Image from "next/image";
import { useState } from "react";
import { Menu, X } from "lucide-react";
import { Button } from "@/components/ui/button";

const navLinks = [
  { href: "/", label: "Home" },
  { href: "/about", label: "About" },
  { href: "/faq", label: "FAQ" },
];

export function Header() {
  const [mobileOpen, setMobileOpen] = useState(false);

  return (
    <header className="sticky top-0 z-50 border-b border-border bg-[#0A0A0A]/95 backdrop-blur-sm">
      <div className="mx-auto flex max-w-7xl items-center justify-between px-4 py-3 sm:px-6 lg:px-8">
        {/* Logo / Brand */}
        <Link href="/" className="flex items-center gap-3">
          <Image
            src="/images/husaynia-logo.png"
            width={40}
            height={40}
            alt="Husaynia Islamic Society"
            className="rounded-full"
          />
          <div className="hidden sm:block">
            <p className="font-heading text-lg font-bold text-gold">
              Husaynia Matchmaking
            </p>
            <p className="text-xs text-muted-foreground">
              Islamic Society of Seattle
            </p>
          </div>
        </Link>

        {/* Desktop Nav */}
        <nav className="hidden md:flex items-center gap-6">
          {navLinks.map((link) => (
            <Link
              key={link.href}
              href={link.href}
              className="text-sm text-muted-foreground transition-colors hover:text-gold"
            >
              {link.label}
            </Link>
          ))}
          <Link href="/register">
            <Button className="bg-primary-red hover:bg-primary-red-light text-white">
              Register Now
            </Button>
          </Link>
        </nav>

        {/* Mobile Menu Toggle */}
        <button
          className="md:hidden text-white"
          onClick={() => setMobileOpen(!mobileOpen)}
          aria-label="Toggle menu"
        >
          {mobileOpen ? <X size={24} /> : <Menu size={24} />}
        </button>
      </div>

      {/* Mobile Nav */}
      {mobileOpen && (
        <nav className="md:hidden border-t border-border bg-[#0A0A0A] px-4 pb-4">
          {navLinks.map((link) => (
            <Link
              key={link.href}
              href={link.href}
              className="block py-3 text-sm text-muted-foreground transition-colors hover:text-gold"
              onClick={() => setMobileOpen(false)}
            >
              {link.label}
            </Link>
          ))}
          <Link href="/register" onClick={() => setMobileOpen(false)}>
            <Button className="mt-2 w-full bg-primary-red hover:bg-primary-red-light text-white">
              Register Now
            </Button>
          </Link>
        </nav>
      )}
    </header>
  );
}
