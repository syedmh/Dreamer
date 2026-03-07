import Link from "next/link";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { XCircle } from "lucide-react";

export default function CancelPage() {
  return (
    <div className="py-20">
      <div className="mx-auto max-w-2xl px-4 text-center">
        <div className="mx-auto mb-6 flex h-20 w-20 items-center justify-center rounded-full bg-karbala-red/20">
          <XCircle className="text-red-400" size={48} />
        </div>

        <h1 className="font-heading text-3xl font-bold text-white mb-4">
          Payment <span className="text-gold">Cancelled</span>
        </h1>

        <p className="text-lg text-muted-foreground mb-8">
          Your payment was not processed. Your registration is not complete until
          payment is received.
        </p>

        <Card className="bg-card border-border mb-8">
          <CardContent className="pt-6 text-sm text-muted-foreground">
            <p>
              If you experienced a technical issue or changed your mind, you can
              try registering again. Your information will need to be re-entered.
            </p>
          </CardContent>
        </Card>

        <div className="flex flex-col sm:flex-row justify-center gap-4">
          <Link href="/register">
            <Button className="bg-primary-red hover:bg-primary-red-light text-white">
              Try Again
            </Button>
          </Link>
          <Link href="/">
            <Button variant="outline" className="border-gold text-gold hover:bg-gold hover:text-black">
              Return to Home
            </Button>
          </Link>
        </div>

        <p className="mt-8 text-xs text-muted-foreground">
          Need help? Contact us at{" "}
          <a href="mailto:contact@husaynia.org" className="text-gold hover:underline">
            contact@husaynia.org
          </a>
        </p>
      </div>
    </div>
  );
}
