import { NextRequest, NextResponse } from "next/server";
import { prisma } from "@/lib/prisma";
import { registrationSchema } from "@/lib/validations";
import { createCheckoutSession } from "@/lib/stripe";

export async function POST(request: NextRequest) {
  // CSRF: verify request origin matches the app
  const origin = request.headers.get("origin");
  const appUrl = process.env.NEXT_PUBLIC_APP_URL || "http://localhost:3000";
  if (origin && !appUrl.startsWith(origin)) {
    return NextResponse.json({ error: "Forbidden" }, { status: 403 });
  }

  try {
    const body = await request.json();

    // Validate form data
    const validationResult = registrationSchema.safeParse(body);
    if (!validationResult.success) {
      return NextResponse.json(
        { error: "Validation failed", details: validationResult.error.flatten() },
        { status: 400 }
      );
    }

    const data = validationResult.data;

    // Check for duplicate email
    const existing = await prisma.registration.findUnique({
      where: { email: data.email },
    });

    if (existing) {
      return NextResponse.json(
        { error: "This email is already registered" },
        { status: 409 }
      );
    }

    // Create registration
    const registration = await prisma.registration.create({
      data: {
        ...data,
        dateOfBirth: new Date(data.dateOfBirth),
        languages: JSON.stringify(data.languages),
        hobbies: JSON.stringify(data.hobbies),
        prefEthnicity: data.prefEthnicity
          ? JSON.stringify(data.prefEthnicity)
          : null,
        status: "PENDING_PAYMENT",
      },
    });

    // Dev mode: skip Stripe and mark as paid immediately (blocked in production)
    if (process.env.DEV_SKIP_PAYMENT === "true" && process.env.NODE_ENV !== "production") {
      await prisma.registration.update({
        where: { id: registration.id },
        data: { status: "PAID" },
      });

      await prisma.paymentRecord.create({
        data: {
          registrationId: registration.id,
          amount: parseInt(process.env.REGISTRATION_FEE_CENTS || "5000"),
          currency: "usd",
          status: "succeeded",
          stripeSessionId: `dev_${registration.id}`,
        },
      });

      return NextResponse.json({
        registrationId: registration.id,
        checkoutUrl: null,
        devMode: true,
      });
    }

    // Production: Create Stripe checkout session
    const checkoutSession = await createCheckoutSession({
      registrationId: registration.id,
      email: data.email,
      name: `${data.firstName} ${data.lastName}`,
    });

    // Update registration with Stripe session ID
    await prisma.registration.update({
      where: { id: registration.id },
      data: { stripeSessionId: checkoutSession.id },
    });

    return NextResponse.json({
      registrationId: registration.id,
      checkoutUrl: checkoutSession.url,
    });
  } catch (error) {
    console.error("Registration error:", error);
    return NextResponse.json(
      { error: "Internal server error" },
      { status: 500 }
    );
  }
}
