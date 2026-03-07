import type { Metadata } from "next";
import { Inter, Montserrat, Amiri } from "next/font/google";
import { Toaster } from "@/components/ui/sonner";
import { Header } from "@/components/layout/Header";
import { Footer } from "@/components/layout/Footer";
import "./globals.css";

const inter = Inter({
  variable: "--font-sans",
  subsets: ["latin"],
});

const montserrat = Montserrat({
  variable: "--font-heading",
  subsets: ["latin"],
});

const amiri = Amiri({
  variable: "--font-arabic",
  weight: ["400", "700"],
  subsets: ["arabic", "latin"],
});

export const metadata: Metadata = {
  title: "Shia Muslim Matchmaking Event | Husaynia Islamic Society of Seattle",
  description:
    "Register for the Shia Muslim Matchmaking Event sponsored by Husaynia Islamic Society of Seattle. Finding Your Better Half, the Halal Way.",
  keywords: [
    "Shia Muslim",
    "matchmaking",
    "marriage",
    "Islamic",
    "Husaynia",
    "Seattle",
  ],
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body
        className={`${inter.variable} ${montserrat.variable} ${amiri.variable} font-sans antialiased`}
      >
        <Header />
        <main className="min-h-screen">{children}</main>
        <Footer />
        <Toaster />
      </body>
    </html>
  );
}
