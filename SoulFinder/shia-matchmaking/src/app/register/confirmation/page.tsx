"use client";

import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import Link from "next/link";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { CheckCircle } from "lucide-react";

function ConfirmationContent() {
  const searchParams = useSearchParams();
  const sessionId = searchParams.get("session_id");

  return (
    <div className="py-20">
      <div className="mx-auto max-w-2xl px-4 text-center">
        <div className="mx-auto mb-6 flex h-20 w-20 items-center justify-center rounded-full bg-primary-red/20">
          <CheckCircle className="text-primary-red-light" size={48} />
        </div>

        <h1 className="font-heading text-3xl font-bold text-white mb-4">
          Registration <span className="text-gold">Confirmed!</span>
        </h1>

        <p className="text-lg text-muted-foreground mb-8">
          Alhamdulillah! Your registration for the Shia Muslim Matchmaking Event
          has been successfully completed.
        </p>

        <Card className="bg-card border-border mb-8">
          <CardContent className="pt-6 text-left space-y-3">
            <div className="flex justify-between text-sm">
              <span className="text-muted-foreground">Payment Status</span>
              <span className="text-primary-red-light font-medium">✓ Paid</span>
            </div>
            {sessionId && (
              <div className="flex justify-between text-sm">
                <span className="text-muted-foreground">Reference</span>
                <span className="text-white font-mono text-xs">
                  {sessionId.slice(0, 20)}...
                </span>
              </div>
            )}
            <div className="flex justify-between text-sm">
              <span className="text-muted-foreground">Amount</span>
              <span className="text-white">$50.00 USD</span>
            </div>
          </CardContent>
        </Card>

        <div className="bg-muted/30 border border-border rounded-lg p-6 mb-8 text-left">
          <h3 className="font-semibold text-white mb-3">What&apos;s Next?</h3>
          <ul className="text-sm text-muted-foreground space-y-2">
            <li>✉️ A confirmation email has been sent to your email address</li>
            <li>📋 Our organizers will review your registration</li>
            <li>📅 You will receive event details and schedule closer to the date</li>
            <li>🤝 Prepare yourself for a blessed journey, InshAllah</li>
          </ul>
        </div>

        <p className="text-xs text-muted-foreground mb-6">
          If you have any questions, contact us at{" "}
          <a href="mailto:contact@husaynia.org" className="text-gold hover:underline">
            contact@husaynia.org
          </a>
        </p>

        <Link href="/">
          <Button className="bg-primary-red hover:bg-primary-red-light text-white">
            Return to Home
          </Button>
        </Link>
      </div>
    </div>
  );
}

export default function ConfirmationPage() {
  return (
    <Suspense
      fallback={
        <div className="py-20 text-center text-muted-foreground">
          Loading confirmation...
        </div>
      }
    >
      <ConfirmationContent />
    </Suspense>
  );
}
