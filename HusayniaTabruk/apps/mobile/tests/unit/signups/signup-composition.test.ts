import { describe, expect, it } from "@jest/globals";

import {
  buildGenericLabel,
  validateSignupComposition,
} from "../../../src/features/signups/signup-composition";

const primaryMembershipId = "primary";

describe("signup composition", () => {
  it("builds only the frozen generic labels", () => {
    expect(buildGenericLabel(null, null)).toBeNull();
    expect(buildGenericLabel("Household", null)).toBe("Household");
    expect(buildGenericLabel("Team", 1)).toBe("Team 1");
    expect(buildGenericLabel("Group", 999)).toBe("Group 999");
    expect(() => buildGenericLabel("Group", 0)).toThrow();
    expect(() => buildGenericLabel("Group", 1000)).toThrow();
  });

  it("accepts only a primary contact for individual signups", () => {
    expect(
      validateSignupComposition({
        kind: "individual",
        labelBase: null,
        labelSuffix: null,
        memberParticipantIds: [],
        primaryMembershipId,
        unnamedParticipantCount: 0,
      }),
    ).toMatchObject({
      totalParticipantCount: 1,
      valid: true,
    });

    expect(
      validateSignupComposition({
        kind: "individual",
        labelBase: null,
        labelSuffix: null,
        memberParticipantIds: ["member-2"],
        primaryMembershipId,
        unnamedParticipantCount: 0,
      }).valid,
    ).toBe(false);
  });

  it("covers household/team 0, 20, and total-25 boundaries", () => {
    const twentyMembers = Array.from(
      { length: 20 },
      (_, index) => `member-${index}`,
    );

    expect(
      validateSignupComposition({
        kind: "household",
        labelBase: "Household",
        labelSuffix: null,
        memberParticipantIds: [],
        primaryMembershipId,
        unnamedParticipantCount: 0,
      }).valid,
    ).toBe(false);
    expect(
      validateSignupComposition({
        kind: "household",
        labelBase: "Household",
        labelSuffix: null,
        memberParticipantIds: [],
        primaryMembershipId,
        unnamedParticipantCount: 1,
      }).valid,
    ).toBe(true);
    expect(
      validateSignupComposition({
        kind: "team",
        labelBase: "Team",
        labelSuffix: 7,
        memberParticipantIds: twentyMembers,
        primaryMembershipId,
        unnamedParticipantCount: 4,
      }),
    ).toMatchObject({
      totalParticipantCount: 25,
      valid: true,
    });
    expect(
      validateSignupComposition({
        kind: "team",
        labelBase: "Team",
        labelSuffix: null,
        memberParticipantIds: twentyMembers,
        primaryMembershipId,
        unnamedParticipantCount: 5,
      }).valid,
    ).toBe(false);
    expect(
      validateSignupComposition({
        kind: "team",
        labelBase: null,
        labelSuffix: null,
        memberParticipantIds: [],
        primaryMembershipId,
        unnamedParticipantCount: 20,
      }).valid,
    ).toBe(true);
  });

  it("rejects duplicate members and the implicit primary contact", () => {
    expect(
      validateSignupComposition({
        kind: "team",
        labelBase: null,
        labelSuffix: null,
        memberParticipantIds: ["member-2", "member-2"],
        primaryMembershipId,
        unnamedParticipantCount: 0,
      }).valid,
    ).toBe(false);
    expect(
      validateSignupComposition({
        kind: "household",
        labelBase: null,
        labelSuffix: null,
        memberParticipantIds: [primaryMembershipId],
        primaryMembershipId,
        unnamedParticipantCount: 0,
      }).valid,
    ).toBe(false);
  });

  it("rejects exactly 21 selected eligible members", () => {
    const twentyOneMembers = Array.from(
      { length: 21 },
      (_, index) => `member-${index}`,
    );

    expect(
      validateSignupComposition({
        kind: "team",
        labelBase: null,
        labelSuffix: null,
        memberParticipantIds: twentyOneMembers,
        primaryMembershipId,
        unnamedParticipantCount: 0,
      }),
    ).toEqual({
      errors: ["Select no more than 20 eligible members."],
      valid: false,
    });
  });

  it("rejects exactly 21 unnamed participants", () => {
    expect(
      validateSignupComposition({
        kind: "household",
        labelBase: null,
        labelSuffix: null,
        memberParticipantIds: [],
        primaryMembershipId,
        unnamedParticipantCount: 21,
      }),
    ).toEqual({
      errors: ["Unnamed participants must be from 0 through 20."],
      valid: false,
    });
  });
});
