import { NextRequest, NextResponse } from "next/server";
import { getStripe } from "@/lib/stripe";
import { prisma } from "@/lib/prisma";
import Stripe from "stripe";

export async function POST(request: NextRequest) {
  const body = await request.text();
  const signature = request.headers.get("stripe-signature");

  if (!signature) {
    return NextResponse.json({ error: "No signature" }, { status: 400 });
  }

  const stripe = getStripe();
  let event: Stripe.Event;

  try {
    event = stripe.webhooks.constructEvent(
      body,
      signature,
      process.env.STRIPE_WEBHOOK_SECRET!
    );
  } catch (err) {
    console.error("Webhook signature verification failed:", err);
    return NextResponse.json(
      { error: "Invalid signature" },
      { status: 400 }
    );
  }

  try {
    switch (event.type) {
      case "checkout.session.completed": {
        const session = event.data.object as Stripe.Checkout.Session;
        const registrationId = session.metadata?.registrationId;

        if (!registrationId) {
          console.error("No registrationId in metadata");
          break;
        }

        // Idempotency: check if already processed
        const existing = await prisma.paymentRecord.findFirst({
          where: { stripeSessionId: session.id, status: "succeeded" },
        });

        if (existing) {
          console.log("Payment already processed:", session.id);
          break;
        }

        // Update registration status
        await prisma.registration.update({
          where: { id: registrationId },
          data: {
            status: "PAID",
            stripeCustomerId: session.customer as string | null,
          },
        });

        // Create payment record
        await prisma.paymentRecord.create({
          data: {
            registrationId,
            stripeSessionId: session.id,
            paymentIntentId: session.payment_intent as string | null,
            amount: session.amount_total || 5000,
            currency: session.currency || "usd",
            status: "succeeded",
          },
        });

        // TODO: Send confirmation email
        console.log("Registration paid:", registrationId);
        break;
      }

      case "checkout.session.expired": {
        const session = event.data.object as Stripe.Checkout.Session;
        const registrationId = session.metadata?.registrationId;

        if (registrationId) {
          await prisma.registration.update({
            where: { id: registrationId },
            data: { status: "EXPIRED" },
          });
        }
        break;
      }

      default:
        console.log("Unhandled event type:", event.type);
    }
  } catch (error) {
    console.error("Webhook processing error:", error);
    return NextResponse.json(
      { error: "Webhook processing failed" },
      { status: 500 }
    );
  }

  return NextResponse.json({ received: true });
}
