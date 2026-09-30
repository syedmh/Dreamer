import type { SubmitSignupRequest } from "../../core/api/auth-api";

export type SignupKind = SubmitSignupRequest["kind"];
export type GenericLabelBase = "Household" | "Team" | "Group" | null;

export interface SignupCompositionInput {
  readonly kind: SignupKind;
  readonly labelBase: GenericLabelBase;
  readonly labelSuffix: number | null;
  readonly memberParticipantIds: readonly string[];
  readonly primaryMembershipId: string;
  readonly unnamedParticipantCount: number;
}

export type SignupCompositionResult =
  | {
      readonly body: SubmitSignupRequest;
      readonly totalParticipantCount: number;
      readonly valid: true;
    }
  | {
      readonly errors: readonly string[];
      readonly valid: false;
    };

export function buildGenericLabel(
  base: GenericLabelBase,
  suffix: number | null,
): string | null {
  if (base === null) {
    return null;
  }

  if (suffix === null) {
    return base;
  }

  if (!Number.isInteger(suffix) || suffix < 1 || suffix > 999) {
    throw new Error("The generic label number must be from 1 through 999.");
  }

  return `${base} ${suffix}`;
}

export function validateSignupComposition(
  input: SignupCompositionInput,
): SignupCompositionResult {
  const errors: string[] = [];
  const uniqueIds = Array.from(new Set(input.memberParticipantIds));

  if (uniqueIds.length !== input.memberParticipantIds.length) {
    errors.push("Each eligible member can be selected only once.");
  }

  if (uniqueIds.includes(input.primaryMembershipId)) {
    errors.push("The primary contact cannot also be a selected participant.");
  }

  if (uniqueIds.length > 20) {
    errors.push("Select no more than 20 eligible members.");
  }

  if (
    !Number.isInteger(input.unnamedParticipantCount) ||
    input.unnamedParticipantCount < 0 ||
    input.unnamedParticipantCount > 20
  ) {
    errors.push("Unnamed participants must be from 0 through 20.");
  }

  let label: string | null = null;
  try {
    label = buildGenericLabel(input.labelBase, input.labelSuffix);
  } catch (error) {
    errors.push(error instanceof Error ? error.message : "The generic label is invalid.");
  }

  const totalParticipantCount =
    1 + uniqueIds.length + input.unnamedParticipantCount;

  if (input.kind === "individual") {
    if (
      uniqueIds.length !== 0 ||
      input.unnamedParticipantCount !== 0 ||
      label !== null
    ) {
      errors.push("Individual signups contain only the primary contact.");
    }
  } else if (totalParticipantCount < 2 || totalParticipantCount > 25) {
    errors.push(
      "Household and team signups must contain from 2 through 25 participants.",
    );
  }

  if (errors.length > 0) {
    return { errors, valid: false };
  }

  return {
    body: {
      kind: input.kind,
      label,
      memberParticipantIds: uniqueIds,
      unnamedParticipantCount: input.unnamedParticipantCount,
    },
    totalParticipantCount,
    valid: true,
  };
}
