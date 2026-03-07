"use client";

import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  familyInfoSchema,
  type FamilyInfoData,
  type RegistrationData,
} from "@/lib/validations";
import {
  FAMILY_SUPPORT_OPTIONS,
  FAMILY_RELIGIOSITY_OPTIONS,
} from "@/types/registration";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Checkbox } from "@/components/ui/checkbox";
import { Textarea } from "@/components/ui/textarea";
import { Button } from "@/components/ui/button";

interface StepProps {
  data: Partial<RegistrationData>;
  onUpdate: (data: FamilyInfoData) => void;
  onNext: () => void;
  onPrev?: () => void;
}

const selectClasses =
  "h-10 w-full rounded-lg border border-input bg-[#1A1A2E] px-3 py-2 text-sm text-white outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 [&>option]:bg-[#1A1A2E] [&>option]:text-white";

export function FamilyInfoStep({ data, onUpdate, onNext, onPrev }: StepProps) {
  const {
    register,
    handleSubmit,
    setValue,
    watch,
    formState: { errors },
  } = useForm<FamilyInfoData>({
    resolver: zodResolver(familyInfoSchema),
    defaultValues: {
      fatherName: data.fatherName ?? "",
      fatherAlive: data.fatherAlive ?? true,
      motherAlive: data.motherAlive ?? true,
      numberOfSiblings: data.numberOfSiblings ?? 0,
      familyOrigin: data.familyOrigin ?? "",
      familySupportive: data.familySupportive ?? "",
      familyReligiosity: data.familyReligiosity ?? "",
      familyExpectations: data.familyExpectations ?? "",
    },
  });

  const fatherAlive = watch("fatherAlive");
  const motherAlive = watch("motherAlive");

  const onSubmit = (formData: FamilyInfoData) => {
    onUpdate(formData);
    onNext();
  };

  return (
    <Card className="border-gold/20 bg-card">
      <CardHeader>
        <CardTitle className="text-gold text-xl">Family Information</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
          {/* Father's Name */}
          <div className="space-y-2">
            <Label htmlFor="fatherName">Father&apos;s Name *</Label>
            <Input id="fatherName" {...register("fatherName")} />
            {errors.fatherName && (
              <p className="text-sm text-destructive">{errors.fatherName.message}</p>
            )}
          </div>

          {/* Parent Status */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label>Father Alive</Label>
              <div className="flex items-center gap-3 pt-1">
                <label className="flex items-center gap-2 cursor-pointer">
                  <Checkbox
                    checked={fatherAlive}
                    onCheckedChange={(checked) =>
                      setValue("fatherAlive", !!checked, { shouldValidate: true })
                    }
                  />
                  <span className="text-sm">Yes</span>
                </label>
              </div>
            </div>

            <div className="space-y-2">
              <Label>Mother Alive</Label>
              <div className="flex items-center gap-3 pt-1">
                <label className="flex items-center gap-2 cursor-pointer">
                  <Checkbox
                    checked={motherAlive}
                    onCheckedChange={(checked) =>
                      setValue("motherAlive", !!checked, { shouldValidate: true })
                    }
                  />
                  <span className="text-sm">Yes</span>
                </label>
              </div>
            </div>
          </div>

          {/* Siblings & Origin */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="numberOfSiblings">Number of Siblings *</Label>
              <Input
                id="numberOfSiblings"
                type="number"
                min={0}
                {...register("numberOfSiblings", { valueAsNumber: true })}
              />
              {errors.numberOfSiblings && (
                <p className="text-sm text-destructive">{errors.numberOfSiblings.message}</p>
              )}
            </div>

            <div className="space-y-2">
              <Label htmlFor="familyOrigin">Family Origin / Ethnicity</Label>
              <Input
                id="familyOrigin"
                placeholder="e.g., Pakistani, Iraqi, Lebanese"
                {...register("familyOrigin")}
              />
            </div>
          </div>

          {/* Family Support & Religiosity */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="familySupportive">Family Supportive of Marriage Search *</Label>
              <select id="familySupportive" {...register("familySupportive")} className={selectClasses}>
                <option value="">Select...</option>
                {FAMILY_SUPPORT_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.familySupportive && (
                <p className="text-sm text-destructive">{errors.familySupportive.message}</p>
              )}
            </div>

            <div className="space-y-2">
              <Label htmlFor="familyReligiosity">Family Religiosity *</Label>
              <select id="familyReligiosity" {...register("familyReligiosity")} className={selectClasses}>
                <option value="">Select...</option>
                {FAMILY_RELIGIOSITY_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.familyReligiosity && (
                <p className="text-sm text-destructive">{errors.familyReligiosity.message}</p>
              )}
            </div>
          </div>

          {/* Family Expectations */}
          <div className="space-y-2">
            <Label htmlFor="familyExpectations">Family Expectations</Label>
            <Textarea
              id="familyExpectations"
              placeholder="Any specific expectations from your family regarding marriage?"
              {...register("familyExpectations")}
              rows={3}
            />
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
