import Link from "next/link";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { IslamicPattern } from "@/components/layout/IslamicPattern";
import {
  ClipboardCheck,
  Users,
  Heart,
  CalendarDays,
  MapPin,
  DollarSign,
  ChevronRight,
} from "lucide-react";

export default function HomePage() {
  return (
    <>
      {/* Hero Section */}
      <section className="relative overflow-hidden py-20 sm:py-32">
        <IslamicPattern />
        <div className="relative mx-auto max-w-7xl px-4 sm:px-6 lg:px-8 text-center">
          {/* Bismillah */}
          <p className="font-arabic text-2xl sm:text-3xl text-gold mb-6" dir="rtl">
            بسم الله الرحمن الرحيم
          </p>

          <h1 className="font-heading text-4xl sm:text-5xl lg:text-6xl font-bold tracking-tight text-white">
            Shia Muslim
            <span className="block text-gold mt-2">Matchmaking Event</span>
          </h1>

          <p className="mx-auto mt-6 max-w-2xl text-lg sm:text-xl text-muted-foreground">
            Finding Your Better Half, the Halal Way
          </p>

          <p className="mt-4 text-sm text-gold/80">
            Proudly sponsored by{" "}
            <a
              href="https://www.husaynia.org"
              target="_blank"
              rel="noopener noreferrer"
              className="underline hover:text-gold"
            >
              Husaynia Islamic Society of Seattle
            </a>
          </p>

          {/* Event Details Cards */}
          <div className="mt-10 flex flex-wrap justify-center gap-4">
            <div className="flex items-center gap-2 rounded-full border border-border bg-card/50 px-4 py-2 text-sm">
              <CalendarDays size={16} className="text-gold" />
              <span className="text-muted-foreground">Date TBA</span>
            </div>
            <div className="flex items-center gap-2 rounded-full border border-border bg-card/50 px-4 py-2 text-sm">
              <MapPin size={16} className="text-gold" />
              <span className="text-muted-foreground">Snohomish, WA</span>
            </div>
            <div className="flex items-center gap-2 rounded-full border border-border bg-card/50 px-4 py-2 text-sm">
              <DollarSign size={16} className="text-gold" />
              <span className="text-muted-foreground">$50 Registration</span>
            </div>
          </div>

          <div className="mt-10 flex flex-col sm:flex-row justify-center gap-4">
            <Link href="/register">
              <Button
                size="lg"
                className="bg-primary-red hover:bg-primary-red-light text-white text-lg px-8 py-6"
              >
                Register Now
                <ChevronRight className="ml-2" size={20} />
              </Button>
            </Link>
            <Link href="/about">
              <Button
                size="lg"
                variant="outline"
                className="border-gold text-gold hover:bg-gold hover:text-black text-lg px-8 py-6"
              >
                Learn More
              </Button>
            </Link>
          </div>
        </div>
      </section>

      {/* Husaynia Quote */}
      <section className="border-y border-border bg-card/30 py-8">
        <div className="mx-auto max-w-4xl px-4 text-center">
          <p className="font-arabic text-xl sm:text-2xl text-gold/90 leading-relaxed" dir="rtl">
            إن لقتل الحُسين حرارةٌ في قلوب المؤمنين لا تبرد أبداً
          </p>
          <p className="mt-2 text-sm text-muted-foreground italic">
            &ldquo;Indeed, the killing of Husayn has placed a heat in the hearts
            of the believers that will never cool.&rdquo;
          </p>
        </div>
      </section>

      {/* About Section */}
      <section className="relative py-20">
        <IslamicPattern />
        <div className="relative mx-auto max-w-7xl px-4 sm:px-6 lg:px-8">
          <h2 className="font-heading text-3xl sm:text-4xl font-bold text-center text-white">
            Why This <span className="text-gold">Event?</span>
          </h2>
          <p className="mx-auto mt-6 max-w-3xl text-center text-muted-foreground leading-relaxed">
            Marriage is half of one&apos;s faith in Islam. The Prophet Muhammad
            (PBUH) said, &ldquo;When a person gets married, they have completed
            half of their religion.&rdquo; This event provides a dignified,
            Islamic, and organized setting for Shia Muslims to find compatible
            life partners, guided by the values of Ahlul Bayt (AS).
          </p>
        </div>
      </section>

      {/* How It Works */}
      <section className="border-y border-border bg-muted/30 py-20">
        <div className="mx-auto max-w-7xl px-4 sm:px-6 lg:px-8">
          <h2 className="font-heading text-3xl sm:text-4xl font-bold text-center text-white mb-12">
            How It <span className="text-gold">Works</span>
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            {[
              {
                icon: ClipboardCheck,
                title: "1. Register",
                desc: "Complete the registration form with your personal, religious, and preference information. Pay the $50 registration fee.",
              },
              {
                icon: Users,
                title: "2. Attend",
                desc: "Join the in-person matchmaking event at Husaynia Islamic Society. Meet potential matches in a respectful, Islamic environment.",
              },
              {
                icon: Heart,
                title: "3. Connect",
                desc: "Our organizers will facilitate introductions based on your preferences and compatibility. Take the next step toward your future together.",
              },
            ].map((step) => (
              <Card
                key={step.title}
                className="bg-card border-border hover:border-gold/50 transition-colors"
              >
                <CardContent className="pt-6 text-center">
                  <div className="mx-auto mb-4 flex h-14 w-14 items-center justify-center rounded-full bg-primary-red/20">
                    <step.icon className="text-gold" size={28} />
                  </div>
                  <h3 className="font-heading text-xl font-semibold text-white">
                    {step.title}
                  </h3>
                  <p className="mt-3 text-sm text-muted-foreground leading-relaxed">
                    {step.desc}
                  </p>
                </CardContent>
              </Card>
            ))}
          </div>
        </div>
      </section>

      {/* FAQ Preview */}
      <section className="relative py-20">
        <IslamicPattern />
        <div className="relative mx-auto max-w-3xl px-4 sm:px-6 lg:px-8">
          <h2 className="font-heading text-3xl sm:text-4xl font-bold text-center text-white mb-12">
            Frequently Asked <span className="text-gold">Questions</span>
          </h2>
          <div className="space-y-6">
            {[
              {
                q: "Who can register?",
                a: "Any Shia Muslim who is 18 years or older and sincerely looking for marriage.",
              },
              {
                q: "Is the $50 fee refundable?",
                a: "No, the registration fee is non-refundable. This ensures only serious participants register.",
              },
              {
                q: "Is my information kept confidential?",
                a: "Absolutely. Your personal information is only shared with event organizers and potential matches through the organizers. It is never made public.",
              },
              {
                q: "Do women need a Wali (Guardian)?",
                a: "Yes, women are required to provide Wali/Guardian information as part of the registration process.",
              },
            ].map((faq) => (
              <Card
                key={faq.q}
                className="bg-card border-border"
              >
                <CardContent className="pt-6">
                  <h3 className="font-semibold text-white">{faq.q}</h3>
                  <p className="mt-2 text-sm text-muted-foreground">{faq.a}</p>
                </CardContent>
              </Card>
            ))}
          </div>
          <div className="mt-8 text-center">
            <Link href="/faq">
              <Button variant="outline" className="border-gold text-gold hover:bg-gold hover:text-black">
                View All FAQs
                <ChevronRight className="ml-1" size={16} />
              </Button>
            </Link>
          </div>
        </div>
      </section>

      {/* CTA */}
      <section className="border-t border-border bg-primary-red/10 py-16">
        <div className="mx-auto max-w-4xl px-4 text-center">
          <h2 className="font-heading text-3xl font-bold text-white">
            Ready to Find Your <span className="text-gold">Better Half?</span>
          </h2>
          <p className="mt-4 text-muted-foreground">
            Take the first step towards a blessed union. Register now for the
            Shia Muslim Matchmaking Event.
          </p>
          <Link href="/register">
            <Button
              size="lg"
              className="mt-8 bg-primary-red hover:bg-primary-red-light text-white text-lg px-10 py-6"
            >
              Register Now — $50
            </Button>
          </Link>
        </div>
      </section>
    </>
  );
}
