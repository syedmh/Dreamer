import Stripe from "stripe";

let _stripe: Stripe | null = null;

export function getStripe(): Stripe {
  if (!_stripe) {
    if (!process.env.STRIPE_SECRET_KEY) {
      throw new Error("STRIPE_SECRET_KEY is not set");
    }
    _stripe = new Stripe(process.env.STRIPE_SECRET_KEY, {
      typescript: true,
    });
  }
  return _stripe;
}

export async function createCheckoutSession({
  registrationId,
  email,
  name,
}: {
  registrationId: string;
  email: string;
  name: string;
}) {
  const stripe = getStripe();
  const appUrl = process.env.NEXT_PUBLIC_APP_URL || "http://localhost:3000";
  if (process.env.NODE_ENV === "production" && appUrl.startsWith("http://")) {
    throw new Error("NEXT_PUBLIC_APP_URL must use HTTPS in production");
  }

  const session = await stripe.checkout.sessions.create({
    payment_method_types: ["card"],
    mode: "payment",
    customer_email: email,
    line_items: [
      {
        price_data: {
          currency: "usd",
          product_data: {
            name: "Shia Matchmaking Event Registration",
            description: "Registration fee for Shia Muslim Matchmaking Event — Husaynia Islamic Society of Seattle",
          },
          unit_amount: parseInt(process.env.REGISTRATION_FEE_CENTS || "5000"),
        },
        quantity: 1,
      },
    ],
    metadata: {
      registrationId,
      email,
      name,
    },
    success_url: `${appUrl}/register/confirmation?session_id={CHECKOUT_SESSION_ID}`,
    cancel_url: `${appUrl}/register/cancel?registration_id=${registrationId}`,
    expires_at: Math.floor(Date.now() / 1000) + 30 * 60, // 30 minutes
  });

  return session;
}
