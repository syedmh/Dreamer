"use client";

import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { STEP_TITLES } from "@/types/registration";
import type { RegistrationData } from "@/lib/validations";
import { Loader2, Edit2 } from "lucide-react";

interface ReviewPaymentStepProps {
  data: Partial<RegistrationData>;
  onEdit: (step: number) => void;
  onSubmit: () => void;
  isSubmitting: boolean;
  error: string | null;
}

function Section({
  title,
  stepIndex,
  onEdit,
  children,
}: {
  title: string;
  stepIndex: number;
  onEdit: (step: number) => void;
  children: React.ReactNode;
}) {
  return (
    <div className="mb-6">
      <div className="flex items-center justify-between mb-3">
        <h3 className="font-heading text-lg font-semibold text-gold">{title}</h3>
        <button
          onClick={() => onEdit(stepIndex)}
          className="flex items-center gap-1 text-xs text-muted-foreground hover:text-gold transition-colors"
        >
          <Edit2 size={12} />
          Edit
        </button>
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-2 text-sm">
        {children}
      </div>
      <Separator className="mt-4 bg-border" />
    </div>
  );
}

function Field({ label, value }: { label: string; value: string | undefined | null }) {
  if (!value) return null;
  return (
    <div>
      <span className="text-muted-foreground">{label}: </span>
      <span className="text-white">{value}</span>
    </div>
  );
}

export function ReviewPaymentStep({
  data,
  onEdit,
  onSubmit,
  isSubmitting,
  error,
}: ReviewPaymentStepProps) {
  return (
    <div>
      <h2 className="font-heading text-xl font-bold text-white mb-6">
        Review Your Information
      </h2>
      <p className="text-sm text-muted-foreground mb-6">
        Please review all your information below. Click &ldquo;Edit&rdquo; on any
        section to make changes before proceeding to payment.
      </p>

      <Section title={STEP_TITLES[0]} stepIndex={0} onEdit={onEdit}>
        <Field label="Name" value={`${data.firstName || ""} ${data.middleName || ""} ${data.lastName || ""}`.trim()} />
        <Field label="Preferred Name" value={data.preferredName} />
        <Field label="Date of Birth" value={data.dateOfBirth} />
        <Field label="Gender" value={data.gender} />
        <Field label="Phone" value={data.phone} />
        <Field label="Email" value={data.email} />
        <Field label="Location" value={`${data.city || ""}, ${data.state || ""}`} />
        <Field label="Country of Origin" value={data.countryOfOrigin} />
        <Field label="Languages" value={data.languages?.join(", ")} />
      </Section>

      <Section title={STEP_TITLES[1]} stepIndex={1} onEdit={onEdit}>
        <Field label="Practicing Muslim" value={data.practicingMuslim} />
        <Field label="Sect" value={data.sect} />
        <Field label="Prayer" value={data.prayerFrequency} />
        <Field label="Fasting" value={data.fastingRamadan} />
        <Field label="Majalis" value={data.attendsMajalis} />
        <Field label="Hijab" value={data.hijabStatus} />
        <Field label="Religious Knowledge" value={data.religiousKnowledge} />
        <Field label="Hajj" value={data.performedHajj} />
        <Field label="Ziyarat" value={data.performedZiyarat} />
        <Field label="Marja" value={data.marja} />
        <Field label="Religion Importance" value={data.religionImportance?.toString()} />
      </Section>

      <Section title={STEP_TITLES[2]} stepIndex={2} onEdit={onEdit}>
        <Field label="Education" value={data.educationLevel} />
        <Field label="Field of Study" value={data.fieldOfStudy} />
        <Field label="University" value={data.university} />
        <Field label="Occupation" value={data.occupation} />
        <Field label="Employer" value={data.employer} />
        <Field label="Income" value={data.incomeRange} />
      </Section>

      <Section title={STEP_TITLES[3]} stepIndex={3} onEdit={onEdit}>
        <Field label="Marital Status" value={data.maritalStatus} />
        <Field label="Children" value={data.hasChildren ? `Yes (${data.numberOfChildren})` : "No"} />
        <Field label="Height" value={data.height} />
        <Field label="Body Type" value={data.bodyType} />
        <Field label="Smoking" value={data.smokingStatus} />
        <Field label="Living" value={data.livingSituation} />
        <Field label="Relocate" value={data.willingToRelocate} />
        <Field label="Hobbies" value={data.hobbies?.join(", ")} />
      </Section>

      <Section title={STEP_TITLES[4]} stepIndex={4} onEdit={onEdit}>
        <Field label="Father" value={data.fatherName} />
        <Field label="Father Alive" value={data.fatherAlive ? "Yes" : "No"} />
        <Field label="Mother Alive" value={data.motherAlive ? "Yes" : "No"} />
        <Field label="Siblings" value={data.numberOfSiblings?.toString()} />
        <Field label="Family Origin" value={data.familyOrigin} />
        <Field label="Family Support" value={data.familySupportive} />
        <Field label="Family Religiosity" value={data.familyReligiosity} />
      </Section>

      <Section title={STEP_TITLES[5]} stepIndex={5} onEdit={onEdit}>
        <Field label="Age Range" value={`${data.prefAgeMin || ""} - ${data.prefAgeMax || ""}`} />
        <Field label="Education Pref" value={data.prefEducation} />
        <Field label="Religiosity Pref" value={data.prefReligiosity} />
        <Field label="Marital Status Pref" value={data.prefMaritalStatus} />
        <Field label="Children Pref" value={data.prefChildren} />
      </Section>

      <Section title={STEP_TITLES[6]} stepIndex={6} onEdit={onEdit}>
        <Field label="Wali Name" value={data.waliName} />
        <Field label="Emergency Contact" value={data.emergencyName} />
        <Field label="Digital Signature" value={data.digitalSignature} />
        <Field
          label="Agreements"
          value={
            data.agreedTerms && data.agreedPrivacy && data.confirmedTruthful
              ? "All accepted ✓"
              : "Incomplete"
          }
        />
      </Section>

      {/* Payment Summary */}
      <div className="bg-muted/30 border border-border rounded-lg p-6 mb-6">
        <div className="flex justify-between items-center">
          <div>
            <h3 className="font-semibold text-white">Registration Fee</h3>
            <p className="text-xs text-muted-foreground">
              Shia Muslim Matchmaking Event — Non-refundable
            </p>
          </div>
          <span className="text-2xl font-bold text-gold">$50.00</span>
        </div>
      </div>

      {error && (
        <div className="bg-red-900/20 border border-red-800 rounded-lg p-4 mb-4">
          <p className="text-sm text-red-400">{error}</p>
        </div>
      )}

      <Button
        onClick={onSubmit}
        disabled={isSubmitting}
        className="w-full bg-primary-red hover:bg-primary-red-light text-white text-lg py-6"
      >
        {isSubmitting ? (
          <>
            <Loader2 className="mr-2 h-5 w-5 animate-spin" />
            Processing...
          </>
        ) : (
          "Proceed to Payment — $50"
        )}
      </Button>

      <p className="text-xs text-center text-muted-foreground mt-4">
        You will be redirected to Stripe for secure payment processing.
      </p>
    </div>
  );
}
