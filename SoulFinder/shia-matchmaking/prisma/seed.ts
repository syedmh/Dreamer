import { PrismaClient } from "@prisma/client";
import { randomBytes, scryptSync } from "crypto";

const prisma = new PrismaClient();

function hashPassword(password: string): string {
  const salt = randomBytes(16).toString("hex");
  const hash = scryptSync(password, salt, 64).toString("hex");
  return `${salt}:${hash}`;
}

async function main() {
  console.log("🌱 Seeding database...");

  // Create a test event
  const event = await prisma.event.upsert({
    where: { id: "default-event" },
    update: {},
    create: {
      id: "default-event",
      name: "Shia Muslim Matchmaking Event 2026",
      date: new Date("2026-06-15T10:00:00Z"),
      location: "Husaynia Islamic Society of Seattle",
      address: "15231 State St, Snohomish, WA 98296, USA",
      price: 5000,
      active: true,
      description:
        "Annual Shia Muslim Matchmaking Event sponsored by Husaynia Islamic Society of Seattle.",
    },
  });
  console.log(`  ✓ Event: ${event.name}`);

  // Create an admin user — password is read from ADMIN_SEED_PASSWORD env var
  const adminPassword = process.env.ADMIN_SEED_PASSWORD;
  if (!adminPassword) {
    if (process.env.NODE_ENV === "production") {
      throw new Error("ADMIN_SEED_PASSWORD env var must be set in production.");
    }
    console.warn("  ⚠️  ADMIN_SEED_PASSWORD not set — skipping admin user seed in development.");
    return;
  }
  const admin = await prisma.adminUser.upsert({
    where: { email: "admin@husaynia.org" },
    update: {},
    create: {
      email: "admin@husaynia.org",
      name: "Admin",
      passwordHash: hashPassword(adminPassword),
      role: "admin",
    },
  });
  console.log(`  ✓ Admin user: ${admin.email}`);

  // Create a sample registration
  const reg = await prisma.registration.upsert({
    where: { email: "sample@test.com" },
    update: {},
    create: {
      status: "PAID",
      eventId: event.id,
      firstName: "Fatima",
      lastName: "Al-Hussaini",
      dateOfBirth: new Date("1998-03-12"),
      gender: "FEMALE",
      phone: "+12065551234",
      email: "sample@test.com",
      city: "Seattle",
      state: "WA",
      countryOfOrigin: "Lebanon",
      languages: JSON.stringify(["English", "Arabic"]),
      practicingMuslim: "Yes",
      sect: "Ithna Ashari/Twelver",
      prayerFrequency: "5 times daily",
      fastingRamadan: "Yes always",
      attendsMajalis: "Regularly",
      hijabStatus: "Yes I wear",
      religiousKnowledge: "Intermediate",
      performedHajj: "No",
      performedZiyarat: "Yes",
      ziyaratLocations: "Karbala, Najaf",
      religionImportance: 5,
      educationLevel: "Bachelor's",
      fieldOfStudy: "Biology",
      university: "University of Washington",
      occupation: "Research Scientist",
      incomeRange: "$75k-$100k",
      maritalStatus: "Never married",
      hasChildren: false,
      height: "5'5",
      smokingStatus: "Never",
      livingSituation: "With parents",
      willingToRelocate: "Maybe",
      hobbies: JSON.stringify(["Reading", "Volunteering", "Islamic studies"]),
      selfDescription:
        "I am a practicing Shia Muslim woman seeking a righteous spouse who values faith, family, and education.",
      fatherName: "Hassan Al-Hussaini",
      fatherAlive: true,
      motherAlive: true,
      numberOfSiblings: 2,
      familyOrigin: "Beirut, Lebanon",
      familySupportive: "Yes",
      familyReligiosity: "Very practicing",
      prefAgeMin: 25,
      prefAgeMax: 35,
      prefReligiosity: "Very practicing",
      lookingForInSpouse:
        "I am looking for a kind, God-fearing man who is committed to the path of Ahlul Bayt (AS). He should value family, be supportive of my career, and want to build a home filled with faith and love.",
      waliName: "Hassan Al-Hussaini",
      waliPhone: "+12065559876",
      waliEmail: "hassan@test.com",
      waliRelationship: "Father",
      emergencyName: "Hassan Al-Hussaini",
      emergencyPhone: "+12065559876",
      emergencyRelation: "Father",
      agreedTerms: true,
      agreedPrivacy: true,
      confirmedTruthful: true,
      agreedNonRefundable: true,
      agreedInfoSharing: true,
      digitalSignature: "Fatima Al-Hussaini",
    },
  });
  console.log(`  ✓ Sample registration: ${reg.firstName} ${reg.lastName}`);

  // Create a payment record for the sample registration
  await prisma.paymentRecord.upsert({
    where: { id: "sample-payment" },
    update: {},
    create: {
      id: "sample-payment",
      registrationId: reg.id,
      amount: 5000,
      currency: "usd",
      status: "succeeded",
      stripeSessionId: "dev_sample",
    },
  });
  console.log("  ✓ Sample payment record");

  console.log("\n✅ Seed complete!");
}

main()
  .catch((e) => {
    console.error("❌ Seed failed:", e);
    process.exit(1);
  })
  .finally(async () => {
    await prisma.$disconnect();
  });
