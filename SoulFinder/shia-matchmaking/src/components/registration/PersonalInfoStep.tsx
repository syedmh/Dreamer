"use client";

import { useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  personalInfoSchema,
  type PersonalInfoData,
  type RegistrationData,
} from "@/lib/validations";
import { LANGUAGES } from "@/types/registration";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Checkbox } from "@/components/ui/checkbox";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { Textarea } from "@/components/ui/textarea";
import { Button } from "@/components/ui/button";

interface StepProps {
  data: Partial<RegistrationData>;
  onUpdate: (data: PersonalInfoData) => void;
  onNext: () => void;
  onPrev?: () => void;
}

export function PersonalInfoStep({ data, onUpdate, onNext, onPrev }: StepProps) {
  const [photoPreview, setPhotoPreview] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    setValue,
    watch,
    formState: { errors },
  } = useForm<PersonalInfoData>({
    resolver: zodResolver(personalInfoSchema),
    defaultValues: {
      firstName: data.firstName ?? "",
      middleName: data.middleName ?? "",
      lastName: data.lastName ?? "",
      preferredName: data.preferredName ?? "",
      dateOfBirth: data.dateOfBirth ?? "",
      gender: data.gender,
      phone: data.phone ?? "",
      email: data.email ?? "",
      city: data.city ?? "",
      state: data.state ?? "",
      countryOfOrigin: data.countryOfOrigin ?? "",
      languages: data.languages ?? [],
      howDidYouHear: data.howDidYouHear ?? "",
    },
  });

  const selectedLanguages = watch("languages");
  const selectedGender = watch("gender");

  const onSubmit = (formData: PersonalInfoData) => {
    onUpdate(formData);
    onNext();
  };

  const toggleLanguage = (lang: string) => {
    const current = selectedLanguages ?? [];
    const updated = current.includes(lang)
      ? current.filter((l) => l !== lang)
      : [...current, lang];
    setValue("languages", updated, { shouldValidate: true });
  };

  const handlePhotoChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) {
      setPhotoPreview(null);
      return;
    }
    if (file.size > 5 * 1024 * 1024) {
      alert("File must be under 5MB");
      e.target.value = "";
      setPhotoPreview(null);
      return;
    }
    const reader = new FileReader();
    reader.onloadend = () => setPhotoPreview(reader.result as string);
    reader.readAsDataURL(file);
  };

  return (
    <Card className="border-gold/20 bg-card">
      <CardHeader>
        <CardTitle className="text-gold text-xl">Personal Information</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
          {/* Name Fields */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="firstName">First Name *</Label>
              <Input id="firstName" {...register("firstName")} />
              {errors.firstName && (
                <p className="text-sm text-destructive">{errors.firstName.message}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="middleName">Middle Name</Label>
              <Input id="middleName" {...register("middleName")} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="lastName">Last Name *</Label>
              <Input id="lastName" {...register("lastName")} />
              {errors.lastName && (
                <p className="text-sm text-destructive">{errors.lastName.message}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="preferredName">Preferred Name</Label>
              <Input id="preferredName" {...register("preferredName")} />
            </div>
          </div>

          {/* Date of Birth */}
          <div className="space-y-2">
            <Label htmlFor="dateOfBirth">Date of Birth *</Label>
            <Input id="dateOfBirth" type="date" {...register("dateOfBirth")} />
            {errors.dateOfBirth && (
              <p className="text-sm text-destructive">{errors.dateOfBirth.message}</p>
            )}
          </div>

          {/* Gender */}
          <div className="space-y-2">
            <Label>Gender *</Label>
            <RadioGroup
              value={selectedGender ?? ""}
              onValueChange={(val) =>
                setValue("gender", val as "MALE" | "FEMALE", { shouldValidate: true })
              }
              className="flex gap-6"
            >
              <div className="flex items-center gap-2">
                <RadioGroupItem value="MALE" />
                <Label>Male</Label>
              </div>
              <div className="flex items-center gap-2">
                <RadioGroupItem value="FEMALE" />
                <Label>Female</Label>
              </div>
            </RadioGroup>
            {errors.gender && (
              <p className="text-sm text-destructive">{errors.gender.message}</p>
            )}
          </div>

          {/* Contact Info */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="phone">Phone *</Label>
              <Input id="phone" type="tel" {...register("phone")} />
              {errors.phone && (
                <p className="text-sm text-destructive">{errors.phone.message}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="email">Email *</Label>
              <Input id="email" type="email" {...register("email")} />
              {errors.email && (
                <p className="text-sm text-destructive">{errors.email.message}</p>
              )}
            </div>
          </div>

          {/* Profile Photo (Optional) */}
          <div className="space-y-2">
            <Label htmlFor="profilePhoto">Profile Photo (Optional)</Label>
            <p className="text-xs text-muted-foreground mb-2">Upload a recent photo (JPEG/PNG, max 5MB)</p>
            <input
              type="file"
              id="profilePhoto"
              accept="image/jpeg,image/png"
              onChange={handlePhotoChange}
              className="block w-full text-sm text-muted-foreground file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-medium file:bg-card file:text-foreground hover:file:bg-muted cursor-pointer"
            />
            {photoPreview && (
              <img
                src={photoPreview}
                alt="Preview"
                className="mt-2 h-24 w-24 rounded-full object-cover border border-border"
              />
            )}
          </div>

          {/* Location */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
            <div className="space-y-2">
              <Label htmlFor="city">City *</Label>
              <Input id="city" {...register("city")} />
              {errors.city && (
                <p className="text-sm text-destructive">{errors.city.message}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="state">State *</Label>
              <Input id="state" {...register("state")} />
              {errors.state && (
                <p className="text-sm text-destructive">{errors.state.message}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="countryOfOrigin">Country of Origin</Label>
              <Input id="countryOfOrigin" {...register("countryOfOrigin")} />
            </div>
          </div>

          {/* Languages */}
          <div className="space-y-2">
            <Label>Languages *</Label>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 md:grid-cols-4">
              {LANGUAGES.map((lang) => (
                <label
                  key={lang}
                  className="flex items-center gap-2 cursor-pointer"
                >
                  <Checkbox
                    checked={selectedLanguages?.includes(lang)}
                    onCheckedChange={() => toggleLanguage(lang)}
                  />
                  <span className="text-sm">{lang}</span>
                </label>
              ))}
            </div>
            {errors.languages && (
              <p className="text-sm text-destructive">{errors.languages.message}</p>
            )}
          </div>

          {/* How Did You Hear */}
          <div className="space-y-2">
            <Label htmlFor="howDidYouHear">How did you hear about us?</Label>
            <Textarea id="howDidYouHear" {...register("howDidYouHear")} rows={2} />
          </div>

          {/* Navigation */}
          <div className="flex justify-between pt-4">
            {onPrev ? (
              <Button type="button" variant="outline" onClick={onPrev}>
                Previous
              </Button>
            ) : (
              <div />
            )}
            <Button type="submit" className="bg-gold text-black hover:bg-gold-light">
              Next
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}
