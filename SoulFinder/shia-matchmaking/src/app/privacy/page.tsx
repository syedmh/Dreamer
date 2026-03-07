import { Card, CardContent } from "@/components/ui/card";

export default function PrivacyPage() {
  return (
    <div className="py-16">
      <div className="mx-auto max-w-4xl px-4 sm:px-6 lg:px-8">
        <h1 className="font-heading text-4xl font-bold text-center text-white mb-12">
          Privacy <span className="text-gold">Policy</span>
        </h1>

        <Card className="bg-card border-border">
          <CardContent className="pt-6 prose prose-invert prose-sm max-w-none">
            <div className="space-y-6 text-muted-foreground text-sm leading-relaxed">
              <section>
                <h2 className="text-xl font-heading font-semibold text-white">1. Information We Collect</h2>
                <p>We collect personal information that you voluntarily provide when registering for the Shia Muslim Matchmaking Event, including but not limited to: name, date of birth, contact information, religious background, education, employment, family details, lifestyle information, partner preferences, and photographs.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">2. How We Use Your Information</h2>
                <p>Your information is used solely for the purpose of facilitating the matchmaking event. This includes:</p>
                <ul className="list-disc list-inside space-y-1 ml-4">
                  <li>Processing your registration and payment</li>
                  <li>Facilitating matches between compatible participants</li>
                  <li>Communicating event details and updates</li>
                  <li>Improving our event organization</li>
                </ul>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">3. Information Sharing</h2>
                <p>Your personal information will only be shared with:</p>
                <ul className="list-disc list-inside space-y-1 ml-4">
                  <li>Event organizers at Husaynia Islamic Society of Seattle</li>
                  <li>Potential matches, only through the organizers (never directly or publicly)</li>
                  <li>Payment processor (Stripe) for transaction processing</li>
                </ul>
                <p className="mt-2">We will never sell, rent, or publicly share your personal information.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">4. Data Security</h2>
                <p>We implement industry-standard security measures to protect your information, including encryption at rest and in transit, secure database storage on Microsoft Azure, and restricted access controls.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">5. Data Retention</h2>
                <p>Your registration data will be retained for one year after the event date, after which it will be securely deleted unless you request earlier deletion.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">6. Your Rights</h2>
                <p>You have the right to:</p>
                <ul className="list-disc list-inside space-y-1 ml-4">
                  <li>Request access to your personal data</li>
                  <li>Request correction of inaccurate data</li>
                  <li>Request deletion of your data</li>
                  <li>Withdraw consent for information sharing</li>
                </ul>
                <p className="mt-2">To exercise any of these rights, contact us at contact@husaynia.org.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">7. Contact</h2>
                <p>For questions about this privacy policy, please contact:</p>
                <p>Husaynia Islamic Society of Seattle<br />15231 State St, Snohomish, WA 98296<br />Email: contact@husaynia.org<br />Phone: +1 (425) 312-3196</p>
              </section>

              <p className="text-xs text-muted-foreground/60 pt-4 border-t border-border">
                Last updated: March 2026
              </p>
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
