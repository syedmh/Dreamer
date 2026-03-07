-- CreateTable
CREATE TABLE "Event" (
    "id" TEXT NOT NULL PRIMARY KEY,
    "name" TEXT NOT NULL,
    "date" DATETIME NOT NULL,
    "location" TEXT NOT NULL,
    "address" TEXT,
    "price" INTEGER NOT NULL DEFAULT 5000,
    "active" BOOLEAN NOT NULL DEFAULT true,
    "description" TEXT,
    "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "updatedAt" DATETIME NOT NULL
);

-- CreateTable
CREATE TABLE "Registration" (
    "id" TEXT NOT NULL PRIMARY KEY,
    "status" TEXT NOT NULL DEFAULT 'DRAFT',
    "eventId" TEXT,
    "firstName" TEXT NOT NULL,
    "middleName" TEXT,
    "lastName" TEXT NOT NULL,
    "preferredName" TEXT,
    "dateOfBirth" DATETIME NOT NULL,
    "gender" TEXT NOT NULL,
    "phone" TEXT NOT NULL,
    "email" TEXT NOT NULL,
    "city" TEXT NOT NULL,
    "state" TEXT NOT NULL,
    "countryOfOrigin" TEXT,
    "languages" TEXT NOT NULL,
    "howDidYouHear" TEXT,
    "practicingMuslim" TEXT,
    "sect" TEXT,
    "prayerFrequency" TEXT,
    "fastingRamadan" TEXT,
    "attendsMajalis" TEXT,
    "hijabStatus" TEXT,
    "religiousKnowledge" TEXT,
    "performedHajj" TEXT,
    "performedZiyarat" TEXT,
    "ziyaratLocations" TEXT,
    "marja" TEXT,
    "religionImportance" INTEGER DEFAULT 5,
    "educationLevel" TEXT,
    "fieldOfStudy" TEXT,
    "university" TEXT,
    "occupation" TEXT,
    "employer" TEXT,
    "incomeRange" TEXT,
    "careerGoals" TEXT,
    "maritalStatus" TEXT,
    "hasChildren" BOOLEAN DEFAULT false,
    "numberOfChildren" INTEGER DEFAULT 0,
    "height" TEXT,
    "bodyType" TEXT,
    "smokingStatus" TEXT,
    "healthConditions" TEXT,
    "livingSituation" TEXT,
    "willingToRelocate" TEXT,
    "pets" TEXT,
    "hobbies" TEXT,
    "selfDescription" TEXT,
    "fatherName" TEXT,
    "fatherAlive" BOOLEAN DEFAULT true,
    "motherAlive" BOOLEAN DEFAULT true,
    "numberOfSiblings" INTEGER DEFAULT 0,
    "familyOrigin" TEXT,
    "familySupportive" TEXT,
    "familyReligiosity" TEXT,
    "familyExpectations" TEXT,
    "prefAgeMin" INTEGER,
    "prefAgeMax" INTEGER,
    "prefHeightMin" TEXT,
    "prefHeightMax" TEXT,
    "prefEthnicity" TEXT,
    "prefEducation" TEXT,
    "prefIncome" TEXT,
    "prefLocation" TEXT,
    "prefMaritalStatus" TEXT,
    "prefChildren" TEXT,
    "prefReligiosity" TEXT,
    "dealBreakers" TEXT,
    "lookingForInSpouse" TEXT,
    "additionalNotes" TEXT,
    "waliName" TEXT,
    "waliPhone" TEXT,
    "waliEmail" TEXT,
    "waliRelationship" TEXT,
    "emergencyName" TEXT,
    "emergencyPhone" TEXT,
    "emergencyRelation" TEXT,
    "agreedTerms" BOOLEAN NOT NULL DEFAULT false,
    "agreedPrivacy" BOOLEAN NOT NULL DEFAULT false,
    "confirmedTruthful" BOOLEAN NOT NULL DEFAULT false,
    "agreedNonRefundable" BOOLEAN NOT NULL DEFAULT false,
    "agreedInfoSharing" BOOLEAN NOT NULL DEFAULT false,
    "digitalSignature" TEXT,
    "stripeCustomerId" TEXT,
    "stripeSessionId" TEXT,
    "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "updatedAt" DATETIME NOT NULL,
    CONSTRAINT "Registration_eventId_fkey" FOREIGN KEY ("eventId") REFERENCES "Event" ("id") ON DELETE SET NULL ON UPDATE CASCADE
);

-- CreateTable
CREATE TABLE "ProfilePhoto" (
    "id" TEXT NOT NULL PRIMARY KEY,
    "registrationId" TEXT NOT NULL,
    "blobUrl" TEXT NOT NULL,
    "thumbnailUrl" TEXT,
    "originalFilename" TEXT NOT NULL,
    "mimeType" TEXT NOT NULL,
    "sizeBytes" INTEGER NOT NULL,
    "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT "ProfilePhoto_registrationId_fkey" FOREIGN KEY ("registrationId") REFERENCES "Registration" ("id") ON DELETE CASCADE ON UPDATE CASCADE
);

-- CreateTable
CREATE TABLE "PaymentRecord" (
    "id" TEXT NOT NULL PRIMARY KEY,
    "registrationId" TEXT NOT NULL,
    "stripeSessionId" TEXT,
    "paymentIntentId" TEXT,
    "amount" INTEGER NOT NULL,
    "currency" TEXT NOT NULL DEFAULT 'usd',
    "status" TEXT NOT NULL,
    "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "updatedAt" DATETIME NOT NULL,
    CONSTRAINT "PaymentRecord_registrationId_fkey" FOREIGN KEY ("registrationId") REFERENCES "Registration" ("id") ON DELETE CASCADE ON UPDATE CASCADE
);

-- CreateTable
CREATE TABLE "AdminUser" (
    "id" TEXT NOT NULL PRIMARY KEY,
    "email" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "passwordHash" TEXT NOT NULL,
    "role" TEXT NOT NULL DEFAULT 'admin',
    "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "updatedAt" DATETIME NOT NULL
);

-- CreateTable
CREATE TABLE "MatchNote" (
    "id" TEXT NOT NULL PRIMARY KEY,
    "registrationId" TEXT NOT NULL,
    "note" TEXT NOT NULL,
    "createdById" TEXT NOT NULL,
    "createdAt" DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    "updatedAt" DATETIME NOT NULL,
    CONSTRAINT "MatchNote_registrationId_fkey" FOREIGN KEY ("registrationId") REFERENCES "Registration" ("id") ON DELETE CASCADE ON UPDATE CASCADE,
    CONSTRAINT "MatchNote_createdById_fkey" FOREIGN KEY ("createdById") REFERENCES "AdminUser" ("id") ON DELETE RESTRICT ON UPDATE CASCADE
);

-- CreateIndex
CREATE UNIQUE INDEX "Registration_email_key" ON "Registration"("email");

-- CreateIndex
CREATE INDEX "Registration_gender_idx" ON "Registration"("gender");

-- CreateIndex
CREATE INDEX "Registration_city_state_idx" ON "Registration"("city", "state");

-- CreateIndex
CREATE INDEX "Registration_maritalStatus_idx" ON "Registration"("maritalStatus");

-- CreateIndex
CREATE INDEX "Registration_status_idx" ON "Registration"("status");

-- CreateIndex
CREATE INDEX "Registration_dateOfBirth_idx" ON "Registration"("dateOfBirth");

-- CreateIndex
CREATE UNIQUE INDEX "ProfilePhoto_registrationId_key" ON "ProfilePhoto"("registrationId");

-- CreateIndex
CREATE INDEX "PaymentRecord_stripeSessionId_idx" ON "PaymentRecord"("stripeSessionId");

-- CreateIndex
CREATE INDEX "PaymentRecord_paymentIntentId_idx" ON "PaymentRecord"("paymentIntentId");

-- CreateIndex
CREATE UNIQUE INDEX "AdminUser_email_key" ON "AdminUser"("email");
