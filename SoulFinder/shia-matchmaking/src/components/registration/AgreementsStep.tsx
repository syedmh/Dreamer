"use client";

import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  agreementsSchema,
  type AgreementsData,
  type RegistrationData,
} from "@/lib/validations";
import { WALI_RELATIONSHIP_OPTIONS } from "@/types/registration";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Checkbox } from "@/components/ui/checkbox";
import { Button } from "@/components/ui/button";

interface StepProps {
  data: Partial<RegistrationData>;
  onUpdate: (data: AgreementsData) => void;
  onNext: () => void;
  onPrev?: () => void;
}

const selectClasses =
  "h-10 w-full rounded-lg border border-input bg-[#1A1A2E] px-3 py-2 text-sm text-white outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 [&>option]:bg-[#1A1A2E] [&>option]:text-white";

export function AgreementsStep({ data, onUpdate, onNext, onPrev }: StepProps) {
  const {
    register,
    handleSubmit,
    setValue,
    watch,
    formState: { errors },
  } = useForm<AgreementsData>({
    resolver: zodResolver(agreementsSchema),
    defaultValues: {
      waliName: data.waliName ?? "",
      waliPhone: data.waliPhone ?? "",
      waliEmail: data.waliEmail ?? "",
      waliRelationship: data.waliRelationship ?? "",
      emergencyName: data.emergencyName ?? "",
      emergencyPhone: data.emergencyPhone ?? "",
      emergencyRelation: data.emergencyRelation ?? "",
      agreedTerms: (data.agreedTerms as true) ?? undefined,
      agreedPrivacy: (data.agreedPrivacy as true) ?? undefined,
      confirmedTruthful: (data.confirmedTruthful as true) ?? undefined,
      agreedNonRefundable: (data.agreedNonRefundable as true) ?? undefined,
      agreedInfoSharing: (data.agreedInfoSharing as true) ?? undefined,
      digitalSignature: data.digitalSignature ?? "",
    },
  });

  const isFemale = data.gender === "FEMALE";

  const agreedTerms = watch("agreedTerms");
  const agreedPrivacy = watch("agreedPrivacy");
  const confirmedTruthful = watch("confirmedTruthful");
  const agreedNonRefundable = watch("agreedNonRefundable");
  const agreedInfoSharing = watch("agreedInfoSharing");

  const onSubmit = (formData: AgreementsData) => {
    onUpdate(formData);
    onNext();
  };

  return (
    <Card className="border-gold/20 bg-card">
      <CardHeader>
        <CardTitle className="text-gold text-xl">Agreements &amp; Guardian</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit(onSubmit)} className="space-y-8">
          {/* Wali Section */}
          <div className="space-y-4">
            <h3 className="text-lg font-medium text-gold/80">
              Wali (Guardian) Information
              {isFemale && <span className="text-destructive"> *</span>}
            </h3>
            {!isFemale && (
              <p className="text-sm text-muted-foreground">
                Optional for male registrants, but encouraged.
              </p>
            )}

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <div className="space-y-2">
                <Label htmlFor="waliName">Wali Name {isFemale && "*"}</Label>
                <Input id="waliName" {...register("waliName")} />
                {errors.waliName && (
                  <p className="text-sm text-destructive">{errors.waliName.message}</p>
                )}
              </div>

              <div className="space-y-2">
                <Label htmlFor="waliRelationship">Relationship {isFemale && "*"}</Label>
                <select id="waliRelationship" {...register("waliRelationship")} className={selectClasses}>
                  <option value="">Select...</option>
                  {WALI_RELATIONSHIP_OPTIONS.map((opt) => (
                    <option key={opt} value={opt}>{opt}</option>
                  ))}
                </select>
                {errors.waliRelationship && (
                  <p className="text-sm text-destructive">{errors.waliRelationship.message}</p>
                )}
              </div>

              <div className="space-y-2">
                <Label htmlFor="waliPhone">Wali Phone {isFemale && "*"}</Label>
                <Input id="waliPhone" type="tel" {...register("waliPhone")} />
                {errors.waliPhone && (
                  <p className="text-sm text-destructive">{errors.waliPhone.message}</p>
                )}
              </div>

              <div className="space-y-2">
                <Label htmlFor="waliEmail">Wali Email</Label>
                <Input id="waliEmail" type="email" {...register("waliEmail")} />
                {errors.waliEmail && (
                  <p className="text-sm text-destructive">{errors.waliEmail.message}</p>
                )}
              </div>
            </div>
          </div>

          {/* Emergency Contact */}
          <div className="space-y-4">
            <h3 className="text-lg font-medium text-gold/80">Emergency Contact *</h3>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <div className="space-y-2">
                <Label htmlFor="emergencyName">Full Name *</Label>
                <Input id="emergencyName" {...register("emergencyName")} />
                {errors.emergencyName && (
                  <p className="text-sm text-destructive">{errors.emergencyName.message}</p>
                )}
              </div>

              <div className="space-y-2">
                <Label htmlFor="emergencyPhone">Phone *</Label>
                <Input id="emergencyPhone" type="tel" {...register("emergencyPhone")} />
                {errors.emergencyPhone && (
                  <p className="text-sm text-destructive">{errors.emergencyPhone.message}</p>
                )}
              </div>

              <div className="space-y-2">
                <Label htmlFor="emergencyRelation">Relationship *</Label>
                <Input
                  id="emergencyRelation"
                  placeholder="e.g., Parent, Sibling, Friend"
                  {...register("emergencyRelation")}
                />
                {errors.emergencyRelation && (
                  <p className="text-sm text-destructive">{errors.emergencyRelation.message}</p>
                )}
              </div>
            </div>
          </div>

          {/* Agreements */}
          <div className="space-y-4">
            <h3 className="text-lg font-medium text-gold/80">Agreements *</h3>
            <div className="space-y-4">
              <label className="flex items-start gap-3 cursor-pointer">
                <Checkbox
                  checked={agreedTerms === true}
                  onCheckedChange={(checked) =>
                    setValue("agreedTerms", checked === true ? true : (undefined as unknown as true), {
                      shouldValidate: true,
                    })
                  }
                  className="mt-0.5"
                />
                <span className="text-sm">
                  I agree to the <span className="text-gold underline">Terms &amp; Conditions</span>
                </span>
              </label>
              {errors.agreedTerms && (
                <p className="text-sm text-destructive">{errors.agreedTerms.message}</p>
              )}

              <label className="flex items-start gap-3 cursor-pointer">
                <Checkbox
                  checked={agreedPrivacy === true}
                  onCheckedChange={(checked) =>
                    setValue("agreedPrivacy", checked === true ? true : (undefined as unknown as true), {
                      shouldValidate: true,
                    })
                  }
                  className="mt-0.5"
                />
                <span className="text-sm">
                  I agree to the <span className="text-gold underline">Privacy Policy</span>
                </span>
              </label>
              {errors.agreedPrivacy && (
                <p className="text-sm text-destructive">{errors.agreedPrivacy.message}</p>
              )}

              <label className="flex items-start gap-3 cursor-pointer">
                <Checkbox
                  checked={confirmedTruthful === true}
                  onCheckedChange={(checked) =>
                    setValue("confirmedTruthful", checked === true ? true : (undefined as unknown as true), {
                      shouldValidate: true,
                    })
                  }
                  className="mt-0.5"
                />
                <span className="text-sm">
                  I confirm that all information provided is truthful and accurate
                </span>
              </label>
              {errors.confirmedTruthful && (
                <p className="text-sm text-destructive">{errors.confirmedTruthful.message}</p>
              )}

              <label className="flex items-start gap-3 cursor-pointer">
                <Checkbox
                  checked={agreedNonRefundable === true}
                  onCheckedChange={(checked) =>
                    setValue("agreedNonRefundable", checked === true ? true : (undefined as unknown as true), {
                      shouldValidate: true,
                    })
                  }
                  className="mt-0.5"
                />
                <span className="text-sm">
                  I acknowledge the registration fee is non-refundable
                </span>
              </label>
              {errors.agreedNonRefundable && (
                <p className="text-sm text-destructive">{errors.agreedNonRefundable.message}</p>
              )}

              <label className="flex items-start gap-3 cursor-pointer">
                <Checkbox
                  checked={agreedInfoSharing === true}
                  onCheckedChange={(checked) =>
                    setValue("agreedInfoSharing", checked === true ? true : (undefined as unknown as true), {
                      shouldValidate: true,
                    })
                  }
                  className="mt-0.5"
                />
                <span className="text-sm">
                  I consent to my information being shared with potential matches
                </span>
              </label>
              {errors.agreedInfoSharing && (
                <p className="text-sm text-destructive">{errors.agreedInfoSharing.message}</p>
              )}
            </div>
          </div>

          {/* Digital Signature */}
          <div className="space-y-2">
            <Label htmlFor="digitalSignature">Digital Signature *</Label>
            <p className="text-sm text-muted-foreground">
              Please type your full legal name as your digital signature.
            </p>
            <Input
              id="digitalSignature"
              placeholder="Type your full name"
              className="font-serif italic"
              {...register("digitalSignature")}
            />
            {errors.digitalSignature && (
              <p className="text-sm text-destructive">{errors.digitalSignature.message}</p>
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
