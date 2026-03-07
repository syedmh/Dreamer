import Link from "next/link";
import { Phone, Mail, MapPin } from "lucide-react";
import { Separator } from "@/components/ui/separator";

export function Footer() {
  return (
    <footer className="border-t border-border bg-[#0A0A0A] gold-border-t">
      <div className="mx-auto max-w-7xl px-4 py-12 sm:px-6 lg:px-8">
        <div className="grid grid-cols-1 gap-8 md:grid-cols-3">
          {/* Brand */}
          <div>
            <h3 className="font-heading text-xl font-bold text-gold">
              Husaynia Islamic Society
            </h3>
            <p className="mt-2 text-sm text-muted-foreground">
              Sponsored by Husaynia Islamic Society of Seattle
            </p>
            <p className="mt-4 font-arabic text-sm text-gold/80 leading-relaxed" dir="rtl">
              إن لقتل الحُسين حرارةٌ في قلوب المؤمنين لا تبرد أبداً
            </p>
          </div>

          {/* Contact */}
          <div>
            <h4 className="font-heading text-lg font-semibold text-white">
              Contact Us
            </h4>
            <ul className="mt-4 space-y-3 text-sm text-muted-foreground">
              <li className="flex items-center gap-2">
                <MapPin size={16} className="text-gold" />
                15231 State St, Snohomish, WA 98296
              </li>
              <li className="flex items-center gap-2">
                <Phone size={16} className="text-gold" />
                <a href="tel:+14253123196" className="hover:text-gold transition-colors">
                  +1 (425) 312-3196
                </a>
              </li>
              <li className="flex items-center gap-2">
                <Mail size={16} className="text-gold" />
                <a href="mailto:contact@husaynia.org" className="hover:text-gold transition-colors">
                  contact@husaynia.org
                </a>
              </li>
            </ul>
          </div>

          {/* Links */}
          <div>
            <h4 className="font-heading text-lg font-semibold text-white">
              Quick Links
            </h4>
            <ul className="mt-4 space-y-2 text-sm text-muted-foreground">
              <li>
                <Link href="/about" className="hover:text-gold transition-colors">
                  About the Event
                </Link>
              </li>
              <li>
                <Link href="/faq" className="hover:text-gold transition-colors">
                  FAQ
                </Link>
              </li>
              <li>
                <Link href="/privacy" className="hover:text-gold transition-colors">
                  Privacy Policy
                </Link>
              </li>
              <li>
                <Link href="/terms" className="hover:text-gold transition-colors">
                  Terms & Conditions
                </Link>
              </li>
              <li>
                <a
                  href="https://www.husaynia.org"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="hover:text-gold transition-colors"
                >
                  Husaynia.org
                </a>
              </li>
            </ul>
          </div>
        </div>

        <Separator className="my-8 bg-border" />

        <div className="flex flex-col items-center justify-between gap-4 sm:flex-row">
          <p className="text-xs text-muted-foreground">
            © {new Date().getFullYear()} Husaynia Islamic Society of Seattle. All rights reserved.
          </p>
          <div className="flex items-center gap-4">
            <a
              href="https://www.facebook.com/husayniaseattle"
              target="_blank"
              rel="noopener noreferrer"
              className="text-xs text-muted-foreground hover:text-gold transition-colors"
            >
              Facebook
            </a>
            <a
              href="https://www.instagram.com/husayniaseattle"
              target="_blank"
              rel="noopener noreferrer"
              className="text-xs text-muted-foreground hover:text-gold transition-colors"
            >
              Instagram
            </a>
            <a
              href="https://www.linkedin.com/company/husayniaseattle"
              target="_blank"
              rel="noopener noreferrer"
              className="text-xs text-muted-foreground hover:text-gold transition-colors"
            >
              LinkedIn
            </a>
          </div>
        </div>
      </div>
    </footer>
  );
}
