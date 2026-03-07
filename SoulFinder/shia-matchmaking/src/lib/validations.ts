import { z } from "zod";

// Step 1: Personal Information
export const personalInfoSchema = z.object({
  firstName: z.string().min(1, "First name is required").max(100),
  middleName: z.string().max(100).optional(),
  lastName: z.string().min(1, "Last name is required").max(100),
  preferredName: z.string().max(100).optional(),
  dateOfBirth: z.string().min(1, "Date of birth is required").refine((val) => {
    const dob = new Date(val);
    const today = new Date();
    const age = today.getFullYear() - dob.getFullYear();
    const monthDiff = today.getMonth() - dob.getMonth();
    const actualAge =
      monthDiff < 0 || (monthDiff === 0 && today.getDate() < dob.getDate())
        ? age - 1
        : age;
    return actualAge >= 18;
  }, "You must be at least 18 years old"),
  gender: z.enum(["MALE", "FEMALE"], {
    message: "Please select your gender",
  }),
  phone: z
    .string()
    .min(10, "Please enter a valid phone number")
    .max(20, "Phone number too long")
    .regex(/^[\d\s\-+()]+$/, "Invalid phone number format"),
  email: z.string().email("Please enter a valid email address"),
  city: z.string().min(1, "City is required"),
  state: z.string().min(1, "State is required"),
  countryOfOrigin: z.string().optional(),
  languages: z
    .array(z.string())
    .min(1, "Please select at least one language"),
  howDidYouHear: z.string().optional(),
});

// Step 2: Religious Background
export const religiousBackgroundSchema = z.object({
  practicingMuslim: z.string().min(1, "This field is required"),
  sect: z.string().min(1, "Please select your sect"),
  prayerFrequency: z.string().min(1, "Please select prayer frequency"),
  fastingRamadan: z.string().min(1, "Please select fasting status"),
  attendsMajalis: z.string().min(1, "Please select"),
  hijabStatus: z.string().min(1, "This field is required"),
  religiousKnowledge: z.string().min(1, "Please select"),
  performedHajj: z.string().min(1, "Please select"),
  performedZiyarat: z.string().min(1, "Please select"),
  ziyaratLocations: z.string().optional(),
  marja: z.string().optional(),
  religionImportance: z.number().min(1).max(5),
});

// Step 3: Education & Career
export const educationCareerSchema = z.object({
  educationLevel: z.string().min(1, "Please select education level"),
  fieldOfStudy: z.string().optional(),
  university: z.string().optional(),
  occupation: z.string().min(1, "Occupation is required"),
  employer: z.string().optional(),
  incomeRange: z.string().min(1, "Please select income range"),
  careerGoals: z.string().optional(),
});

// Step 4: Lifestyle & Personal
export const lifestyleSchema = z.object({
  maritalStatus: z.string().min(1, "Please select marital status"),
  hasChildren: z.boolean(),
  numberOfChildren: z.number().min(0).optional(),
  height: z.string().min(1, "Height is required"),
  bodyType: z.string().optional(),
  smokingStatus: z.string().min(1, "Please select"),
  healthConditions: z.string().optional(),
  livingSituation: z.string().min(1, "Please select"),
  willingToRelocate: z.string().min(1, "Please select"),
  pets: z.string().optional(),
  hobbies: z.array(z.string()).min(1, "Select at least one hobby"),
  selfDescription: z
    .string()
    .min(50, "Please write at least 50 characters about yourself")
    .max(5000, "Self description must be under 5000 characters"),
});

// Step 5: Family Information
export const familyInfoSchema = z.object({
  fatherName: z.string().min(1, "Father's name is required"),
  fatherAlive: z.boolean(),
  motherAlive: z.boolean(),
  numberOfSiblings: z.number().min(0),
  familyOrigin: z.string().optional(),
  familySupportive: z.string().min(1, "Please select"),
  familyReligiosity: z.string().min(1, "Please select"),
  familyExpectations: z.string().optional(),
});

// Step 6: Partner Preferences
export const partnerPreferencesSchema = z.object({
  prefAgeMin: z.number().min(18, "Minimum age must be 18+"),
  prefAgeMax: z.number().max(100),
  prefHeightMin: z.string().optional(),
  prefHeightMax: z.string().optional(),
  prefEthnicity: z.array(z.string()).optional(),
  prefEducation: z.string().optional(),
  prefIncome: z.string().optional(),
  prefLocation: z.string().optional(),
  prefMaritalStatus: z.string().optional(),
  prefChildren: z.string().optional(),
  prefReligiosity: z.string().optional(),
  dealBreakers: z.string().optional(),
  lookingForInSpouse: z
    .string()
    .min(100, "Please write at least 100 characters")
    .max(5000, "Must be under 5000 characters"),
  additionalNotes: z.string().optional(),
});

// Step 7: Agreements & Wali
export const agreementsSchema = z.object({
  waliName: z.string().optional(),
  waliPhone: z.string().optional(),
  waliEmail: z.string().email().optional().or(z.literal("")),
  waliRelationship: z.string().optional(),
  emergencyName: z.string().min(1, "Emergency contact name is required"),
  emergencyPhone: z.string().min(1, "Emergency contact phone is required"),
  emergencyRelation: z.string().min(1, "Relationship is required"),
  agreedTerms: z.literal(true, "You must agree to Terms & Conditions"),
  agreedPrivacy: z.literal(true, "You must agree to Privacy Policy"),
  confirmedTruthful: z.literal(true, "You must confirm all information is truthful"),
  agreedNonRefundable: z.literal(true, "You must acknowledge the non-refundable fee"),
  agreedInfoSharing: z.literal(true, "You must consent to information sharing"),
  digitalSignature: z.string().min(2, "Please type your full name as signature"),
});

// Full registration schema
export const registrationSchema = personalInfoSchema
  .merge(religiousBackgroundSchema)
  .merge(educationCareerSchema)
  .merge(lifestyleSchema)
  .merge(familyInfoSchema)
  .merge(partnerPreferencesSchema)
  .merge(agreementsSchema);

export type PersonalInfoData = z.infer<typeof personalInfoSchema>;
export type ReligiousBackgroundData = z.infer<typeof religiousBackgroundSchema>;
export type EducationCareerData = z.infer<typeof educationCareerSchema>;
export type LifestyleData = z.infer<typeof lifestyleSchema>;
export type FamilyInfoData = z.infer<typeof familyInfoSchema>;
export type PartnerPreferencesData = z.infer<typeof partnerPreferencesSchema>;
export type AgreementsData = z.infer<typeof agreementsSchema>;
export type RegistrationData = z.infer<typeof registrationSchema>;
