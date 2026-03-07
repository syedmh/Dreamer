"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Card, CardContent } from "@/components/ui/card";
import { IslamicPattern } from "@/components/layout/IslamicPattern";
import { ProgressBar } from "@/components/registration/ProgressBar";
import { PersonalInfoStep } from "@/components/registration/PersonalInfoStep";
import { ReligiousBackgroundStep } from "@/components/registration/ReligiousBackgroundStep";
import { EducationCareerStep } from "@/components/registration/EducationCareerStep";
import { LifestyleStep } from "@/components/registration/LifestyleStep";
import { FamilyInfoStep } from "@/components/registration/FamilyInfoStep";
import { PartnerPreferencesStep } from "@/components/registration/PartnerPreferencesStep";
import { AgreementsStep } from "@/components/registration/AgreementsStep";
import { ReviewPaymentStep } from "@/components/registration/ReviewPaymentStep";
import { useRegistrationForm } from "@/hooks/useRegistrationForm";

export default function RegisterPage() {
  const router = useRouter();
  const {
    currentStep,
    formData,
    isSubmitting,
    setIsSubmitting,
    updateFormData,
    nextStep,
    prevStep,
    goToStep,
  } = useRegistrationForm();
  const [submitError, setSubmitError] = useState<string | null>(null);

  const handleSubmit = async () => {
    setIsSubmitting(true);
    setSubmitError(null);

    try {
      const response = await fetch("/api/register", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(formData),
      });

      const result = await response.json();

      if (!response.ok) {
        setSubmitError(result.error || "Registration failed. Please try again.");
        setIsSubmitting(false);
        return;
      }

      // Redirect to Stripe Checkout
      if (result.checkoutUrl) {
        window.location.href = result.checkoutUrl;
      } else {
        router.push("/register/confirmation");
      }
    } catch {
      setSubmitError("An unexpected error occurred. Please try again.");
      setIsSubmitting(false);
    }
  };

  const renderStep = () => {
    const commonProps = {
      data: formData,
      onUpdate: updateFormData,
      onNext: nextStep,
      onPrev: prevStep,
    };

    switch (currentStep) {
      case 0:
        return <PersonalInfoStep {...commonProps} onPrev={undefined} />;
      case 1:
        return <ReligiousBackgroundStep {...commonProps} />;
      case 2:
        return <EducationCareerStep {...commonProps} />;
      case 3:
        return <LifestyleStep {...commonProps} />;
      case 4:
        return <FamilyInfoStep {...commonProps} />;
      case 5:
        return <PartnerPreferencesStep {...commonProps} />;
      case 6:
        return <AgreementsStep {...commonProps} />;
      case 7:
        return (
          <ReviewPaymentStep
            data={formData}
            onEdit={goToStep}
            onSubmit={handleSubmit}
            isSubmitting={isSubmitting}
            error={submitError}
          />
        );
      default:
        return null;
    }
  };

  return (
    <div className="relative py-12">
      <IslamicPattern />
      <div className="relative mx-auto max-w-3xl px-4 sm:px-6 lg:px-8">
        <div className="text-center mb-8">
          <p className="font-arabic text-lg text-gold mb-2" dir="rtl">
            بسم الله الرحمن الرحيم
          </p>
          <h1 className="font-heading text-3xl font-bold text-white">
            Event <span className="text-gold">Registration</span>
          </h1>
          <p className="mt-2 text-sm text-muted-foreground">
            Complete all steps to register for the Shia Muslim Matchmaking Event
          </p>
        </div>

        <ProgressBar currentStep={currentStep} onStepClick={goToStep} />

        <Card className="bg-card border-border">
          <CardContent className="pt-6">{renderStep()}</CardContent>
        </Card>
      </div>
    </div>
  );
}
