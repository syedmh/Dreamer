"use client";

import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  partnerPreferencesSchema,
  type PartnerPreferencesData,
  type RegistrationData,
} from "@/lib/validations";
import {
  EDUCATION_LEVELS,
  INCOME_RANGES,
  MARITAL_STATUS_OPTIONS,
  PRACTICING_OPTIONS,
} from "@/types/registration";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Button } from "@/components/ui/button";

interface StepProps {
  data: Partial<RegistrationData>;
  onUpdate: (data: PartnerPreferencesData) => void;
  onNext: () => void;
  onPrev?: () => void;
}

const selectClasses =
  "h-10 w-full rounded-lg border border-input bg-[#1A1A2E] px-3 py-2 text-sm text-white outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 [&>option]:bg-[#1A1A2E] [&>option]:text-white";

export function PartnerPreferencesStep({ data, onUpdate, onNext, onPrev }: StepProps) {
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<PartnerPreferencesData>({
    resolver: zodResolver(partnerPreferencesSchema),
    defaultValues: {
      prefAgeMin: data.prefAgeMin ?? 18,
      prefAgeMax: data.prefAgeMax ?? 40,
      prefHeightMin: data.prefHeightMin ?? "",
      prefHeightMax: data.prefHeightMax ?? "",
      prefEthnicity: data.prefEthnicity ?? [],
      prefEducation: data.prefEducation ?? "",
      prefIncome: data.prefIncome ?? "",
      prefLocation: data.prefLocation ?? "",
      prefMaritalStatus: data.prefMaritalStatus ?? "",
      prefChildren: data.prefChildren ?? "",
      prefReligiosity: data.prefReligiosity ?? "",
      dealBreakers: data.dealBreakers ?? "",
      lookingForInSpouse: data.lookingForInSpouse ?? "",
      additionalNotes: data.additionalNotes ?? "",
    },
  });

  const onSubmit = (formData: PartnerPreferencesData) => {
    onUpdate(formData);
    onNext();
  };

  return (
    <Card className="border-gold/20 bg-card">
      <CardHeader>
        <CardTitle className="text-gold text-xl">Partner Preferences</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
          {/* Age Range */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="prefAgeMin">Preferred Age (Min) *</Label>
              <Input
                id="prefAgeMin"
                type="number"
                min={18}
                {...register("prefAgeMin", { valueAsNumber: true })}
              />
              {errors.prefAgeMin && (
                <p className="text-sm text-destructive">{errors.prefAgeMin.message}</p>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="prefAgeMax">Preferred Age (Max) *</Label>
              <Input
                id="prefAgeMax"
                type="number"
                max={100}
                {...register("prefAgeMax", { valueAsNumber: true })}
              />
              {errors.prefAgeMax && (
                <p className="text-sm text-destructive">{errors.prefAgeMax.message}</p>
              )}
            </div>
          </div>

          {/* Height Range */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="prefHeightMin">Preferred Height (Min)</Label>
              <Input id="prefHeightMin" placeholder={"e.g., 5'0\""} {...register("prefHeightMin")} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="prefHeightMax">Preferred Height (Max)</Label>
              <Input id="prefHeightMax" placeholder={"e.g., 6'2\""} {...register("prefHeightMax")} />
            </div>
          </div>

          {/* Ethnicity */}
          <div className="space-y-2">
            <Label htmlFor="prefEthnicity">Preferred Ethnicity</Label>
            <Input
              id="prefEthnicity"
              placeholder="e.g., Any, South Asian, Arab, Persian (comma-separated)"
              onChange={(e) => {
                const value = e.target.value;
                const arr = value ? value.split(",").map((s) => s.trim()).filter(Boolean) : [];
                register("prefEthnicity").onChange({
                  target: { name: "prefEthnicity", value: arr },
                });
              }}
              defaultValue={data.prefEthnicity?.join(", ") ?? ""}
            />
          </div>

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Education */}
            <div className="space-y-2">
              <Label htmlFor="prefEducation">Preferred Education Level</Label>
              <select id="prefEducation" {...register("prefEducation")} className={selectClasses}>
                <option value="">No preference</option>
                {EDUCATION_LEVELS.map((level) => (
                  <option key={level} value={level}>{level}</option>
                ))}
              </select>
            </div>

            {/* Income */}
            <div className="space-y-2">
              <Label htmlFor="prefIncome">Preferred Income Range</Label>
              <select id="prefIncome" {...register("prefIncome")} className={selectClasses}>
                <option value="">No preference</option>
                {INCOME_RANGES.map((range) => (
                  <option key={range} value={range}>{range}</option>
                ))}
              </select>
            </div>

            {/* Location */}
            <div className="space-y-2">
              <Label htmlFor="prefLocation">Preferred Location</Label>
              <Input
                id="prefLocation"
                placeholder="e.g., Same city, Same state, Anywhere"
                {...register("prefLocation")}
              />
            </div>

            {/* Marital Status */}
            <div className="space-y-2">
              <Label htmlFor="prefMaritalStatus">Preferred Marital Status</Label>
              <select id="prefMaritalStatus" {...register("prefMaritalStatus")} className={selectClasses}>
                <option value="">No preference</option>
                {MARITAL_STATUS_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
            </div>

            {/* Children Preference */}
            <div className="space-y-2">
              <Label htmlFor="prefChildren">Accepts Children</Label>
              <select id="prefChildren" {...register("prefChildren")} className={selectClasses}>
                <option value="">No preference</option>
                <option value="Yes">Yes</option>
                <option value="No">No</option>
              </select>
            </div>

            {/* Religiosity */}
            <div className="space-y-2">
              <Label htmlFor="prefReligiosity">Preferred Religiosity</Label>
              <select id="prefReligiosity" {...register("prefReligiosity")} className={selectClasses}>
                <option value="">No preference</option>
                {PRACTICING_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
            </div>
          </div>

          {/* Deal Breakers */}
          <div className="space-y-2">
            <Label htmlFor="dealBreakers">Deal Breakers</Label>
            <Textarea
              id="dealBreakers"
              placeholder="List any absolute deal breakers..."
              {...register("dealBreakers")}
              rows={3}
            />
          </div>

          {/* What You're Looking For */}
          <div className="space-y-2">
            <Label htmlFor="lookingForInSpouse">What You&apos;re Looking For in a Spouse *</Label>
            <Textarea
              id="lookingForInSpouse"
              placeholder="Describe the qualities and values you seek in a life partner (minimum 100 characters)..."
              {...register("lookingForInSpouse")}
              rows={5}
            />
            {errors.lookingForInSpouse && (
              <p className="text-sm text-destructive">{errors.lookingForInSpouse.message}</p>
            )}
          </div>

          {/* Additional Notes */}
          <div className="space-y-2">
            <Label htmlFor="additionalNotes">Additional Notes</Label>
            <Textarea
              id="additionalNotes"
              placeholder="Anything else you'd like us to know?"
              {...register("additionalNotes")}
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
