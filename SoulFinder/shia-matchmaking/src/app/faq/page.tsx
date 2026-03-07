import { Card, CardContent } from "@/components/ui/card";
import { IslamicPattern } from "@/components/layout/IslamicPattern";

const faqs = [
  {
    q: "Who can register for this event?",
    a: "Any Shia Muslim who is 18 years or older, regardless of ethnicity or nationality, and is sincerely seeking marriage.",
  },
  {
    q: "What is the $50 registration fee for?",
    a: "The registration fee covers event organization, venue costs, and ensures that only serious participants register. It is non-refundable.",
  },
  {
    q: "Is the $50 fee refundable?",
    a: "No, the registration fee is strictly non-refundable. This is clearly stated during the registration process and must be acknowledged before payment.",
  },
  {
    q: "How is my personal information protected?",
    a: "All personal information is stored securely and encrypted. It is only accessible to event organizers and will only be shared with potential matches through the organizers — never publicly.",
  },
  {
    q: "Do women need a Wali (Guardian)?",
    a: "Yes, women are required to provide Wali/Guardian information (name, phone, email, and relationship) as part of the Islamic guidelines for the marriage process.",
  },
  {
    q: "What information do I need to provide?",
    a: "The registration form covers personal information, religious background, education & career, lifestyle, family information, partner preferences, and agreements. A profile photo is also required.",
  },
  {
    q: "Can I edit my registration after submitting?",
    a: "Once submitted and paid, you cannot directly edit your registration. Please contact us at contact@husaynia.org to request changes.",
  },
  {
    q: "What happens after I register?",
    a: "After payment, you'll receive a confirmation email with your registration ID and event details. The organizers will review your profile and facilitate potential matches at the event.",
  },
  {
    q: "Can I register if I am divorced or widowed?",
    a: "Absolutely. The event welcomes never married, divorced, widowed, and annulled individuals.",
  },
  {
    q: "What if I don't find a match at the event?",
    a: "Not every participant will find an immediate match, and that's okay. The organizers may continue to facilitate introductions after the event based on the profiles collected.",
  },
  {
    q: "Is there an age limit?",
    a: "You must be at least 18 years old to register. There is no upper age limit.",
  },
  {
    q: "Can my family attend the event?",
    a: "Details about family attendance will be shared closer to the event date. The event is designed to be family-friendly and culturally appropriate.",
  },
];

export default function FAQPage() {
  return (
    <div className="relative py-16">
      <IslamicPattern />
      <div className="relative mx-auto max-w-3xl px-4 sm:px-6 lg:px-8">
        <h1 className="font-heading text-4xl font-bold text-center text-white mb-12">
          Frequently Asked <span className="text-gold">Questions</span>
        </h1>

        <div className="space-y-4">
          {faqs.map((faq) => (
            <Card key={faq.q} className="bg-card border-border">
              <CardContent className="pt-6">
                <h3 className="font-semibold text-white">{faq.q}</h3>
                <p className="mt-2 text-sm text-muted-foreground leading-relaxed">
                  {faq.a}
                </p>
              </CardContent>
            </Card>
          ))}
        </div>
      </div>
    </div>
  );
}
