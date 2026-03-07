"use client";

import { useState, useCallback } from "react";
import type { RegistrationData } from "@/lib/validations";

const defaultFormData: Partial<RegistrationData> = {
  firstName: "",
  middleName: "",
  lastName: "",
  preferredName: "",
  dateOfBirth: "",
  gender: undefined,
  phone: "",
  email: "",
  city: "",
  state: "",
  countryOfOrigin: "",
  languages: [],
  howDidYouHear: "",
  practicingMuslim: "",
  sect: "",
  prayerFrequency: "",
  fastingRamadan: "",
  attendsMajalis: "",
  hijabStatus: "",
  religiousKnowledge: "",
  performedHajj: "",
  performedZiyarat: "",
  ziyaratLocations: "",
  marja: "",
  religionImportance: 5,
  educationLevel: "",
  fieldOfStudy: "",
  university: "",
  occupation: "",
  employer: "",
  incomeRange: "",
  careerGoals: "",
  maritalStatus: "",
  hasChildren: false,
  numberOfChildren: 0,
  height: "",
  bodyType: "",
  smokingStatus: "",
  healthConditions: "",
  livingSituation: "",
  willingToRelocate: "",
  pets: "",
  hobbies: [],
  selfDescription: "",
  fatherName: "",
  fatherAlive: true,
  motherAlive: true,
  numberOfSiblings: 0,
  familyOrigin: "",
  familySupportive: "",
  familyReligiosity: "",
  familyExpectations: "",
  prefAgeMin: 18,
  prefAgeMax: 50,
  prefHeightMin: "",
  prefHeightMax: "",
  prefEthnicity: [],
  prefEducation: "",
  prefIncome: "",
  prefLocation: "",
  prefMaritalStatus: "",
  prefChildren: "",
  prefReligiosity: "",
  dealBreakers: "",
  lookingForInSpouse: "",
  additionalNotes: "",
  waliName: "",
  waliPhone: "",
  waliEmail: "",
  waliRelationship: "",
  emergencyName: "",
  emergencyPhone: "",
  emergencyRelation: "",
  agreedTerms: false as unknown as true,
  agreedPrivacy: false as unknown as true,
  confirmedTruthful: false as unknown as true,
  agreedNonRefundable: false as unknown as true,
  agreedInfoSharing: false as unknown as true,
  digitalSignature: "",
};

export function useRegistrationForm() {
  const [currentStep, setCurrentStep] = useState(0);
  const [formData, setFormData] = useState<Partial<RegistrationData>>(defaultFormData);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const updateFormData = useCallback(
    (data: Partial<RegistrationData>) => {
      setFormData((prev) => ({ ...prev, ...data }));
    },
    []
  );

  const nextStep = useCallback(() => {
    setCurrentStep((prev) => Math.min(prev + 1, 7));
  }, []);

  const prevStep = useCallback(() => {
    setCurrentStep((prev) => Math.max(prev - 1, 0));
  }, []);

  const goToStep = useCallback((step: number) => {
    setCurrentStep(step);
  }, []);

  return {
    currentStep,
    formData,
    isSubmitting,
    setIsSubmitting,
    updateFormData,
    nextStep,
    prevStep,
    goToStep,
  };
}
