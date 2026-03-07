import { Card, CardContent } from "@/components/ui/card";

export default function TermsPage() {
  return (
    <div className="py-16">
      <div className="mx-auto max-w-4xl px-4 sm:px-6 lg:px-8">
        <h1 className="font-heading text-4xl font-bold text-center text-white mb-12">
          Terms & <span className="text-gold">Conditions</span>
        </h1>

        <Card className="bg-card border-border">
          <CardContent className="pt-6 prose prose-invert prose-sm max-w-none">
            <div className="space-y-6 text-muted-foreground text-sm leading-relaxed">
              <section>
                <h2 className="text-xl font-heading font-semibold text-white">1. Eligibility</h2>
                <p>To register for the Shia Muslim Matchmaking Event, you must:</p>
                <ul className="list-disc list-inside space-y-1 ml-4">
                  <li>Be at least 18 years of age</li>
                  <li>Be a Shia Muslim sincerely seeking marriage</li>
                  <li>Provide truthful and accurate information</li>
                  <li>Pay the non-refundable $50 registration fee</li>
                </ul>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">2. Registration Fee</h2>
                <p>The registration fee of $50 USD is <strong className="text-white">non-refundable</strong> under any circumstances, including but not limited to: inability to attend the event, change of mind, dissatisfaction with matches, or event cancellation due to circumstances beyond our control.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">3. Accuracy of Information</h2>
                <p>You certify that all information provided in your registration is truthful and accurate. Providing false information may result in disqualification from the event without refund.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">4. Confidentiality</h2>
                <p>Your personal information will be treated as confidential. It will only be shared with event organizers and potential matches through the organizers. You agree not to publicly share information about other participants obtained during the event.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">5. Code of Conduct</h2>
                <p>All participants are expected to:</p>
                <ul className="list-disc list-inside space-y-1 ml-4">
                  <li>Behave respectfully and in accordance with Islamic values</li>
                  <li>Maintain appropriate boundaries at all times</li>
                  <li>Respect the privacy and dignity of all participants</li>
                  <li>Follow the guidance and instructions of event organizers</li>
                </ul>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">6. No Guarantee of Match</h2>
                <p>Husaynia Islamic Society of Seattle does not guarantee that you will find a match at this event. The organizers will make their best effort to facilitate compatible introductions, but the outcome depends on many factors beyond our control.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">7. Liability</h2>
                <p>Husaynia Islamic Society of Seattle is not liable for any outcomes resulting from connections made at this event. Participants engage at their own discretion and are encouraged to perform their own due diligence.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">8. Guardian (Wali) Requirement</h2>
                <p>Female participants are required to provide Wali (Guardian) information as part of the registration. This is in accordance with Islamic guidelines for the marriage process.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">9. Photo Requirement</h2>
                <p>A recent, clear profile photo is required for registration. This photo will only be used for matchmaking purposes and will not be publicly displayed.</p>
              </section>

              <section>
                <h2 className="text-xl font-heading font-semibold text-white">10. Contact</h2>
                <p>For questions about these terms, contact:</p>
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
