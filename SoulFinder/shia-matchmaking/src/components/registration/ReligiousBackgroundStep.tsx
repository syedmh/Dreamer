"use client";

import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  religiousBackgroundSchema,
  type ReligiousBackgroundData,
  type RegistrationData,
} from "@/lib/validations";
import {
  PRACTICING_OPTIONS,
  SECT_OPTIONS,
  PRAYER_FREQUENCY,
  FASTING_OPTIONS,
  MAJALIS_OPTIONS,
  HIJAB_WOMEN,
  HIJAB_MEN,
  RELIGIOUS_KNOWLEDGE,
  HAJJ_OPTIONS,
} from "@/types/registration";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Button } from "@/components/ui/button";

interface StepProps {
  data: Partial<RegistrationData>;
  onUpdate: (data: ReligiousBackgroundData) => void;
  onNext: () => void;
  onPrev?: () => void;
}

const selectClasses =
  "h-10 w-full rounded-lg border border-input bg-[#1A1A2E] px-3 py-2 text-sm text-white outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 [&>option]:bg-[#1A1A2E] [&>option]:text-white";

export function ReligiousBackgroundStep({ data, onUpdate, onNext, onPrev }: StepProps) {
  const {
    register,
    handleSubmit,
    setValue,
    watch,
    formState: { errors },
  } = useForm<ReligiousBackgroundData>({
    resolver: zodResolver(religiousBackgroundSchema),
    defaultValues: {
      practicingMuslim: data.practicingMuslim ?? "",
      sect: data.sect ?? "",
      prayerFrequency: data.prayerFrequency ?? "",
      fastingRamadan: data.fastingRamadan ?? "",
      attendsMajalis: data.attendsMajalis ?? "",
      hijabStatus: data.hijabStatus ?? "",
      religiousKnowledge: data.religiousKnowledge ?? "",
      performedHajj: data.performedHajj ?? "",
      performedZiyarat: data.performedZiyarat ?? "",
      ziyaratLocations: data.ziyaratLocations ?? "",
      marja: data.marja ?? "",
      religionImportance: data.religionImportance ?? 0,
    },
  });

  const performedZiyarat = watch("performedZiyarat");
  const religionImportance = watch("religionImportance");
  const gender = data.gender;
  const hijabOptions = gender === "FEMALE" ? HIJAB_WOMEN : HIJAB_MEN;

  const onSubmit = (formData: ReligiousBackgroundData) => {
    onUpdate(formData);
    onNext();
  };

  return (
    <Card className="border-gold/20 bg-card">
      <CardHeader>
        <CardTitle className="text-gold text-xl">Religious Background</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit(onSubmit)} className="space-y-6">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Practicing Muslim */}
            <div className="space-y-2">
              <Label htmlFor="practicingMuslim">Practicing Muslim *</Label>
              <select id="practicingMuslim" {...register("practicingMuslim")} className={selectClasses}>
                <option value="">Select...</option>
                {PRACTICING_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.practicingMuslim && (
                <p className="text-sm text-destructive">{errors.practicingMuslim.message}</p>
              )}
            </div>

            {/* Sect */}
            <div className="space-y-2">
              <Label htmlFor="sect">Sect *</Label>
              <select id="sect" {...register("sect")} className={selectClasses}>
                <option value="">Select...</option>
                {SECT_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.sect && (
                <p className="text-sm text-destructive">{errors.sect.message}</p>
              )}
            </div>

            {/* Prayer Frequency */}
            <div className="space-y-2">
              <Label htmlFor="prayerFrequency">Prayer Frequency *</Label>
              <select id="prayerFrequency" {...register("prayerFrequency")} className={selectClasses}>
                <option value="">Select...</option>
                {PRAYER_FREQUENCY.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.prayerFrequency && (
                <p className="text-sm text-destructive">{errors.prayerFrequency.message}</p>
              )}
            </div>

            {/* Fasting */}
            <div className="space-y-2">
              <Label htmlFor="fastingRamadan">Fasting in Ramadan *</Label>
              <select id="fastingRamadan" {...register("fastingRamadan")} className={selectClasses}>
                <option value="">Select...</option>
                {FASTING_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.fastingRamadan && (
                <p className="text-sm text-destructive">{errors.fastingRamadan.message}</p>
              )}
            </div>

            {/* Attends Majalis */}
            <div className="space-y-2">
              <Label htmlFor="attendsMajalis">Attends Majalis *</Label>
              <select id="attendsMajalis" {...register("attendsMajalis")} className={selectClasses}>
                <option value="">Select...</option>
                {MAJALIS_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.attendsMajalis && (
                <p className="text-sm text-destructive">{errors.attendsMajalis.message}</p>
              )}
            </div>

            {/* Hijab Status */}
            <div className="space-y-2">
              <Label htmlFor="hijabStatus">
                {gender === "FEMALE" ? "Hijab Status" : "Hijab Preference"} *
              </Label>
              <select id="hijabStatus" {...register("hijabStatus")} className={selectClasses}>
                <option value="">Select...</option>
                {hijabOptions.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.hijabStatus && (
                <p className="text-sm text-destructive">{errors.hijabStatus.message}</p>
              )}
            </div>

            {/* Religious Knowledge */}
            <div className="space-y-2">
              <Label htmlFor="religiousKnowledge">Religious Knowledge *</Label>
              <select id="religiousKnowledge" {...register("religiousKnowledge")} className={selectClasses}>
                <option value="">Select...</option>
                {RELIGIOUS_KNOWLEDGE.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.religiousKnowledge && (
                <p className="text-sm text-destructive">{errors.religiousKnowledge.message}</p>
              )}
            </div>

            {/* Hajj */}
            <div className="space-y-2">
              <Label htmlFor="performedHajj">Performed Hajj *</Label>
              <select id="performedHajj" {...register("performedHajj")} className={selectClasses}>
                <option value="">Select...</option>
                {HAJJ_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.performedHajj && (
                <p className="text-sm text-destructive">{errors.performedHajj.message}</p>
              )}
            </div>
          </div>

          {/* Ziyarat */}
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="performedZiyarat">Performed Ziyarat *</Label>
              <select id="performedZiyarat" {...register("performedZiyarat")} className={selectClasses}>
                <option value="">Select...</option>
                {HAJJ_OPTIONS.map((opt) => (
                  <option key={opt} value={opt}>{opt}</option>
                ))}
              </select>
              {errors.performedZiyarat && (
                <p className="text-sm text-destructive">{errors.performedZiyarat.message}</p>
              )}
            </div>

            {performedZiyarat === "Yes" && (
              <div className="space-y-2">
                <Label htmlFor="ziyaratLocations">Ziyarat Locations</Label>
                <Input
                  id="ziyaratLocations"
                  placeholder="e.g., Karbala, Najaf, Mashhad"
                  {...register("ziyaratLocations")}
                />
              </div>
            )}
          </div>

          {/* Marja */}
          <div className="space-y-2">
            <Label htmlFor="marja">Marja (Religious Authority)</Label>
            <Input
              id="marja"
              placeholder="e.g., Ayatollah Sistani"
              {...register("marja")}
            />
          </div>

          {/* Religion Importance */}
          <div className="space-y-2">
            <Label>Importance of Religion in Your Life *</Label>
            <div className="flex flex-wrap gap-2 sm:gap-3">
              {[1, 2, 3, 4, 5].map((val) => (
                <button
                  key={val}
                  type="button"
                  onClick={() => setValue("religionImportance", val, { shouldValidate: true })}
                  className={`flex h-10 w-10 sm:h-12 sm:w-12 items-center justify-center rounded-lg border text-sm font-medium transition-colors ${
                    religionImportance === val
                      ? "border-gold bg-gold text-black"
                      : "border-border hover:border-gold/50"
                  }`}
                >
                  {val}
                </button>
              ))}
            </div>
            <p className="text-xs text-muted-foreground">1 = Not very important, 5 = Extremely important</p>
            {errors.religionImportance && (
              <p className="text-sm text-destructive">{errors.religionImportance.message}</p>
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
