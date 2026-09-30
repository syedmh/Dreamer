import type { MeResponse, TokenSetResponse } from "../api/generated/api-contract-client";

export interface AuthCredentials {
  readonly email: string;
  readonly password: string;
}

export interface AuthSession extends TokenSetResponse {
  readonly actor: MeResponse;
  readonly installationId: string;
}
