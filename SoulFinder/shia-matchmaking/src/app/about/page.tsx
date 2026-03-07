import { IslamicPattern } from "@/components/layout/IslamicPattern";
import { Card, CardContent } from "@/components/ui/card";

export default function AboutPage() {
  return (
    <div className="relative py-16">
      <IslamicPattern />
      <div className="relative mx-auto max-w-4xl px-4 sm:px-6 lg:px-8">
        <p className="text-center font-arabic text-xl text-gold mb-4" dir="rtl">
          بسم الله الرحمن الرحيم
        </p>
        <h1 className="font-heading text-4xl font-bold text-center text-white mb-12">
          About the <span className="text-gold">Event</span>
        </h1>

        <div className="space-y-8">
          <Card className="bg-card border-border">
            <CardContent className="pt-6">
              <h2 className="font-heading text-2xl font-semibold text-gold mb-4">
                Our Mission
              </h2>
              <p className="text-muted-foreground leading-relaxed">
                The Shia Muslim Matchmaking Event is organized to provide a dignified,
                Islamic, and organized platform for Shia Muslims to find compatible life
                partners. In a world where finding a righteous spouse can be challenging,
                this event creates a respectful environment guided by the teachings of
                Ahlul Bayt (AS).
              </p>
            </CardContent>
          </Card>

          <Card className="bg-card border-border">
            <CardContent className="pt-6">
              <h2 className="font-heading text-2xl font-semibold text-gold mb-4">
                About Husaynia Islamic Society
              </h2>
              <p className="text-muted-foreground leading-relaxed mb-4">
                Husaynia Islamic Society of Seattle is a vibrant Islamic center serving
                the Shia Muslim community in the greater Seattle area. Located in
                Snohomish, Washington, we are dedicated to preserving and promoting the
                teachings of the Prophet Muhammad (PBUH) and his Ahlul Bayt (AS).
              </p>
              <div className="text-sm text-muted-foreground space-y-1">
                <p>📍 15231 State St, Snohomish, WA 98296, USA</p>
                <p>📞 +1 (425) 312-3196</p>
                <p>✉️ contact@husaynia.org</p>
                <p>
                  🌐{" "}
                  <a
                    href="https://www.husaynia.org"
                    className="text-gold hover:underline"
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    www.husaynia.org
                  </a>
                </p>
              </div>
            </CardContent>
          </Card>

          <Card className="bg-card border-border">
            <CardContent className="pt-6">
              <h2 className="font-heading text-2xl font-semibold text-gold mb-4">
                Islamic Perspective on Marriage
              </h2>
              <p className="text-muted-foreground leading-relaxed mb-4">
                Marriage holds an exalted position in Islam. The Prophet Muhammad (PBUH)
                said: &ldquo;When a person gets married, they have completed half of
                their religion, so let them fear Allah in the remaining half.&rdquo;
              </p>
              <p className="text-muted-foreground leading-relaxed">
                Imam Ali (AS) advised: &ldquo;Marriage is a fortress, so protect
                yourselves with it.&rdquo; This event aims to help believing men and
                women build those sacred bonds in accordance with the Sunnah of the
                Prophet (PBUH) and the guidance of the Ahlul Bayt (AS).
              </p>
            </CardContent>
          </Card>

          <Card className="bg-card border-border">
            <CardContent className="pt-6">
              <h2 className="font-heading text-2xl font-semibold text-gold mb-4">
                What to Expect
              </h2>
              <ul className="text-muted-foreground space-y-3">
                <li className="flex items-start gap-2">
                  <span className="text-gold mt-1">●</span>
                  A respectful, gender-conscious environment
                </li>
                <li className="flex items-start gap-2">
                  <span className="text-gold mt-1">●</span>
                  Organized matchmaking facilitated by experienced community members
                </li>
                <li className="flex items-start gap-2">
                  <span className="text-gold mt-1">●</span>
                  Complete confidentiality of all personal information
                </li>
                <li className="flex items-start gap-2">
                  <span className="text-gold mt-1">●</span>
                  Opportunity to meet potential matches with family involvement
                </li>
                <li className="flex items-start gap-2">
                  <span className="text-gold mt-1">●</span>
                  Islamic guidance and support throughout the process
                </li>
              </ul>
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
