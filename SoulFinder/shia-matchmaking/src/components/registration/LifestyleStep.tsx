"use client";

import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  lifestyleSchema,
  type LifestyleData,
  type RegistrationData,
} from "@/lib/validations";
import {
  MARITAL_STATUS_OPTIONS,
  SMOKING_OPTIONS,
  LIVING_SITUATION,
  RELOCATE_OPTIONS,
  BODY_TYPE_OPTIONS,
  HOBBIES,
} from "@/types/registration";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Checkbox } from "@/components/ui/checkbox";
import { Textarea } from "@/components/ui/textarea";
import { Button } from "@/components/ui/button";

interface StepProps {
  data: Partial<RegistrationData>;
  onUpdate: (data: LifestyleData) => void;
  onNext: () => void;
  onPrev?: () => void;
}

const selectClasses =
  "h-10 w-full rounded-lg border border-input bg-[#1A1A2E] px-3 py-2 text-sm text-white outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 [&>option]:bg-[#1A1A2E] [&>option]:text-white";

export function LifestyleStep({ data, onUpdate, onNext, onPrev }: StepProps) {
  const {
    register,
    handleSubmit,
    setValue,
    watch,
    formState: { errors },
  } = useForm<LifestyleData>({
    resolver: zodResolver(lifestyleSchema),
    defaultValues: {
      maritalStatus: data.maritalStatus ?? "",
      hasChildren: data.hasChildren ?? false,
      numberOfChildren: data.numberOfChildren ?? 0,
      height: data.height ?? "",
      bodyType: data.bodyType ?? "",
      smokingStatus: data.smokingStatus ?? "",
      healthConditions: data.healthConditions ?? "",
      livingSituation: data.livingSituation ?? "",
      willingToRelocate: data.willingToRelocate ?? "",
      pets: data.pets ?? "",
      hobbies: data.hobbies ?? [],
      selfDescription: data.selfDescription ?? "",
    },
  });

  const hasChildren = watch("hasChildren");
  const selectedHobbies = watch("hobbies");

  const onSubmit = (formData: LifestyleData) => {
    onUpdate(formData);
    onNext();
  };

  const toggleHobby = (hobby: string) => {
    const current = selectedHobbies ?? [];
    const updated = current.includes(hobby)
      ? current.filter((h) => h !== hobby)
      : [...current, hobby];
    setValue("hobbies", updated, { shouldValidate: true });
  };

  return (
    <Card className="border-gold/20 bg-card">
      <CardHeader>
        <CardTitle className="text-gold text-xl">Lifestyle &amp; Personal</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Marital Status */}
            <div className="space-y-2">
              <Label htmlFor="maritalStatus">Marital Status *</Label>
              <select id="maritalStatus" {...register("maritalStatus")} className={selectClasses}>
                <option value="">Select...</option>
                {MARITAL_STATUS_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.maritalStatus && (
                <p className="text-sm text-destructive">{errors.maritalStatus.message}</p>
              )}
            </div>

            {/* Has Children */}
            <div className="space-y-2">
              <Label>Has Children</Label>
              <div className="flex items-center gap-3 pt-1">
                <label className="flex items-center gap-2 cursor-pointer">
                  <Checkbox
                    checked={hasChildren}
                    onCheckedChange={(checked) =>
                      setValue("hasChildren", !!checked, { shouldValidate: true })
                    }
                  />
                  <span className="text-sm">Yes, I have children</span>
                </label>
              </div>
            </div>

            {/* Number of Children */}
            {hasChildren && (
              <div className="space-y-2">
                <Label htmlFor="numberOfChildren">Number of Children</Label>
                <Input
                  id="numberOfChildren"
                  type="number"
                  min={0}
                  {...register("numberOfChildren", { valueAsNumber: true })}
                />
              </div>
            )}

            {/* Height */}
            <div className="space-y-2">
              <Label htmlFor="height">Height *</Label>
              <Input id="height" placeholder={"e.g., 5'8\" or 173cm"} {...register("height")} />
              {errors.height && (
                <p className="text-sm text-destructive">{errors.height.message}</p>
              )}
            </div>

            {/* Body Type */}
            <div className="space-y-2">
              <Label htmlFor="bodyType">Body Type</Label>
              <select id="bodyType" {...register("bodyType")} className={selectClasses}>
                <option value="">Select...</option>
                {BODY_TYPE_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
            </div>

            {/* Smoking Status */}
            <div className="space-y-2">
              <Label htmlFor="smokingStatus">Smoking Status *</Label>
              <select id="smokingStatus" {...register("smokingStatus")} className={selectClasses}>
                <option value="">Select...</option>
                {SMOKING_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.smokingStatus && (
                <p className="text-sm text-destructive">{errors.smokingStatus.message}</p>
              )}
            </div>

            {/* Living Situation */}
            <div className="space-y-2">
              <Label htmlFor="livingSituation">Living Situation *</Label>
              <select id="livingSituation" {...register("livingSituation")} className={selectClasses}>
                <option value="">Select...</option>
                {LIVING_SITUATION.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.livingSituation && (
                <p className="text-sm text-destructive">{errors.livingSituation.message}</p>
              )}
            </div>

            {/* Willing to Relocate */}
            <div className="space-y-2">
              <Label htmlFor="willingToRelocate">Willing to Relocate *</Label>
              <select id="willingToRelocate" {...register("willingToRelocate")} className={selectClasses}>
                <option value="">Select...</option>
                {RELOCATE_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.willingToRelocate && (
                <p className="text-sm text-destructive">{errors.willingToRelocate.message}</p>
              )}
            </div>
          </div>

          {/* Health Conditions */}
          <div className="space-y-2">
            <Label htmlFor="healthConditions">Health Conditions</Label>
            <Input
              id="healthConditions"
              placeholder="Any health conditions to note (optional)"
              {...register("healthConditions")}
            />
          </div>

          {/* Pets */}
          <div className="space-y-2">
            <Label htmlFor="pets">Pets</Label>
            <Input
              id="pets"
              placeholder="Do you have any pets? (optional)"
              {...register("pets")}
            />
          </div>

          {/* Hobbies */}
          <div className="space-y-2">
            <Label>Hobbies &amp; Interests *</Label>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 md:grid-cols-4">
              {HOBBIES.map((hobby) => (
                <label
                  key={hobby}
                  className="flex items-center gap-2 cursor-pointer"
                >
                  <Checkbox
                    checked={selectedHobbies?.includes(hobby)}
                    onCheckedChange={() => toggleHobby(hobby)}
                  />
                  <span className="text-sm">{hobby}</span>
                </label>
              ))}
            </div>
            {errors.hobbies && (
              <p className="text-sm text-destructive">{errors.hobbies.message}</p>
            )}
          </div>

          {/* Self Description */}
          <div className="space-y-2">
            <Label htmlFor="selfDescription">About Yourself *</Label>
            <Textarea
              id="selfDescription"
              placeholder="Tell us about yourself (minimum 50 characters)..."
              {...register("selfDescription")}
              rows={4}
            />
            {errors.selfDescription && (
              <p className="text-sm text-destructive">{errors.selfDescription.message}</p>
            )}
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
