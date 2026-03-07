"use client";

import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  educationCareerSchema,
  type EducationCareerData,
  type RegistrationData,
} from "@/lib/validations";
import { EDUCATION_LEVELS, INCOME_RANGES } from "@/types/registration";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Button } from "@/components/ui/button";

interface StepProps {
  data: Partial<RegistrationData>;
  onUpdate: (data: EducationCareerData) => void;
  onNext: () => void;
  onPrev?: () => void;
}

const selectClasses =
  "h-10 w-full rounded-lg border border-input bg-[#1A1A2E] px-3 py-2 text-sm text-white outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 [&>option]:bg-[#1A1A2E] [&>option]:text-white";

export function EducationCareerStep({ data, onUpdate, onNext, onPrev }: StepProps) {
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<EducationCareerData>({
    resolver: zodResolver(educationCareerSchema),
    defaultValues: {
      educationLevel: data.educationLevel ?? "",
      fieldOfStudy: data.fieldOfStudy ?? "",
      university: data.university ?? "",
      occupation: data.occupation ?? "",
      employer: data.employer ?? "",
      incomeRange: data.incomeRange ?? "",
      careerGoals: data.careerGoals ?? "",
    },
  });

  const onSubmit = (formData: EducationCareerData) => {
    onUpdate(formData);
    onNext();
  };

  return (
    <Card className="border-gold/20 bg-card">
      <CardHeader>
        <CardTitle className="text-gold text-xl">Education &amp; Career</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Education Level */}
            <div className="space-y-2">
              <Label htmlFor="educationLevel">Education Level *</Label>
              <select id="educationLevel" {...register("educationLevel")} className={selectClasses}>
                <option value="">Select...</option>
                {EDUCATION_LEVELS.map((level) => (
                  <option key={level} value={level}>{level}</option>
                ))}
              </select>
              {errors.educationLevel && (
                <p className="text-sm text-destructive">{errors.educationLevel.message}</p>
              )}
            </div>

            {/* Field of Study */}
            <div className="space-y-2">
              <Label htmlFor="fieldOfStudy">Field of Study</Label>
              <Input id="fieldOfStudy" {...register("fieldOfStudy")} />
            </div>

            {/* University */}
            <div className="space-y-2">
              <Label htmlFor="university">University / Institution</Label>
              <Input id="university" {...register("university")} />
            </div>

            {/* Occupation */}
            <div className="space-y-2">
              <Label htmlFor="occupation">Occupation *</Label>
              <Input id="occupation" {...register("occupation")} />
              {errors.occupation && (
                <p className="text-sm text-destructive">{errors.occupation.message}</p>
              )}
            </div>

            {/* Employer */}
            <div className="space-y-2">
              <Label htmlFor="employer">Employer</Label>
              <Input id="employer" {...register("employer")} />
            </div>

            {/* Income Range */}
            <div className="space-y-2">
              <Label htmlFor="incomeRange">Income Range *</Label>
              <select id="incomeRange" {...register("incomeRange")} className={selectClasses}>
                <option value="">Select...</option>
                {INCOME_RANGES.map((range) => (
                  <option key={range} value={range}>{range}</option>
                ))}
              </select>
              {errors.incomeRange && (
                <p className="text-sm text-destructive">{errors.incomeRange.message}</p>
              )}
            </div>
          </div>

          {/* Career Goals */}
          <div className="space-y-2">
            <Label htmlFor="careerGoals">Career Goals</Label>
            <Textarea
              id="careerGoals"
              placeholder="Share your career aspirations..."
              {...register("careerGoals")}
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
