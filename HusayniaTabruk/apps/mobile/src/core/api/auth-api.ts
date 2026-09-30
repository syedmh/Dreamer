import {
  ApiClientError,
  GeneratedApiClient,
  type ApiSuccessEnvelope,
  type ApproveSignupRequest,
  type CancelServiceDateRequest,
  type CloseServiceDateRequest,
  type CursorPageOfEligibleSignupParticipantResponse,
  type CursorPageOfServiceDateResponse,
  type CursorPageOfSignupResponse,
  type CursorQuery,
  type DeclineSignupRequest,
  type HelpNeedResponse,
  type LoginRequest,
  type LogoutRequest,
  type MeResponse,
  type OverrideSignupRequest,
  type PatchHelpNeedRequest,
  type PatchServiceDateRequest,
  type PostThreadMessageRequest,
  type PrivilegedThreadMessagePageResponse,
  type PrivilegedThreadReadRequest,
  type ProblemDetails,
  type ReasonRequest,
  type ReassignSignupRequest,
  type RefreshRequest,
  type ReportThreadMessageRequest,
  type RosterResponse,
  type ServiceDateResponse,
  type SignupResponse,
  type SubmitSignupRequest,
  type ThreadMessagePageResponse,
  type ThreadMessageResponse,
  type ThreadStateResponse,
  type TokenSetResponse,
  type WaitlistSignupRequest,
  type WithdrawSignupRequest,
} from "./generated/api-contract-client";

const installationIdHeader = "X-Installation-Id";

export type {
  ApiSuccessEnvelope,
  ApproveSignupRequest,
  CancelServiceDateRequest,
  CloseServiceDateRequest,
  CursorPageOfEligibleSignupParticipantResponse,
  CursorPageOfServiceDateResponse,
  CursorPageOfSignupResponse,
  CursorQuery,
  DeclineSignupRequest,
  HelpNeedResponse,
  LoginRequest,
  LogoutRequest,
  MeResponse,
  OverrideSignupRequest,
  PatchHelpNeedRequest,
  PatchServiceDateRequest,
  PostThreadMessageRequest,
  PrivilegedThreadMessagePageResponse,
  PrivilegedThreadReadRequest,
  ProblemDetails,
  ReasonRequest,
  ReassignSignupRequest,
  RefreshRequest,
  ReportThreadMessageRequest,
  RosterResponse,
  ServiceDateResponse,
  SignupResponse,
  SubmitSignupRequest,
  ThreadMessagePageResponse,
  ThreadMessageResponse,
  ThreadStateResponse,
  TokenSetResponse,
  WaitlistSignupRequest,
  WithdrawSignupRequest,
};
export { ApiClientError };

export interface AuthApi {
  getCurrentActor(accessToken: string, signal?: AbortSignal): Promise<MeResponse>;
  login(
    request: LoginRequest,
    installationId: string,
    signal?: AbortSignal,
  ): Promise<TokenSetResponse>;
  logoutSession(
    refreshToken: string,
    installationId: string,
    signal?: AbortSignal,
  ): Promise<void>;
  refreshSession(
    refreshToken: string,
    installationId: string,
    signal?: AbortSignal,
  ): Promise<TokenSetResponse>;
}

export interface T14Api {
  getManagedRoster(
    accessToken: string,
    dateId: string,
    query?: CursorQuery,
    signal?: AbortSignal,
  ): Promise<RosterResponse>;
  getServiceDate(
    accessToken: string,
    dateId: string,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse>;
  listEligibleSignupParticipants(
    accessToken: string,
    query?: CursorQuery,
    signal?: AbortSignal,
  ): Promise<CursorPageOfEligibleSignupParticipantResponse>;
  listMySignups(
    accessToken: string,
    query?: CursorQuery,
    signal?: AbortSignal,
  ): Promise<CursorPageOfSignupResponse>;
  listOpenServiceDates(
    accessToken: string,
    query?: CursorQuery,
    signal?: AbortSignal,
  ): Promise<CursorPageOfServiceDateResponse>;
  submitSignup(
    accessToken: string,
    needId: string,
    idempotencyKey: string,
    body: SubmitSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse>;
}

export interface T18Api extends T14Api {
  approveSignup(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: ApproveSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse>;
  cancelServiceDate(
    accessToken: string,
    dateId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: CancelServiceDateRequest,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse>;
  closeServiceDate(
    accessToken: string,
    dateId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: CloseServiceDateRequest,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse>;
  declineSignup(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: DeclineSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse>;
  editHelpNeed(
    accessToken: string,
    needId: string,
    expectedVersion: number,
    body: PatchHelpNeedRequest,
    signal?: AbortSignal,
  ): Promise<HelpNeedResponse>;
  editServiceDate(
    accessToken: string,
    dateId: string,
    expectedVersion: number,
    body: PatchServiceDateRequest,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse>;
  openServiceDate(
    accessToken: string,
    dateId: string,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse>;
  overrideSignupCancellation(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: OverrideSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse>;
  reassignWaitlistedSignup(
    accessToken: string,
    needId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: ReassignSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse>;
  waitlistSignup(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: WaitlistSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse>;
  withdrawSignup(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: WithdrawSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse>;
}

export interface T19Api extends T18Api {
  hideThreadMessage(
    accessToken: string,
    dateId: string,
    messageId: string,
    expectedVersion: number,
    body: ReasonRequest,
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<ThreadMessageResponse>>;
  listThreadMessages(
    accessToken: string,
    dateId: string,
    query?: CursorQuery,
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<ThreadMessagePageResponse>>;
  lockThread(
    accessToken: string,
    dateId: string,
    expectedVersion: number,
    body: ReasonRequest,
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<ThreadStateResponse>>;
  postThreadMessage(
    accessToken: string,
    dateId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: PostThreadMessageRequest,
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<ThreadMessageResponse>>;
  readPrivilegedThreadMessages(
    accessToken: string,
    stepUpToken: string,
    body: PrivilegedThreadReadRequest,
    signal?: AbortSignal,
  ): Promise<PrivilegedThreadMessagePageResponse>;
  reportThreadMessage(
    accessToken: string,
    dateId: string,
    messageId: string,
    expectedVersion: number,
    body: ReportThreadMessageRequest,
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<void>>;
}

export class MobileApiConfigurationError extends Error {
  public constructor() {
    super("The mobile client is missing EXPO_PUBLIC_API_BASE_URL.");
    this.name = "MobileApiConfigurationError";
  }
}

export class GeneratedAuthApi implements AuthApi, T14Api, T18Api, T19Api {
  private readonly baseUrl: string;
  private readonly client: GeneratedApiClient;

  public constructor(baseUrl: string, fetcher?: typeof fetch) {
    this.baseUrl = normalizeBaseUrl(baseUrl);
    this.client = new GeneratedApiClient({
      baseUrl: this.baseUrl,
      fetch: fetcher,
    });
  }

  public async getCurrentActor(
    accessToken: string,
    signal?: AbortSignal,
  ): Promise<MeResponse> {
    this.ensureConfigured();
    return this.client.getCurrentActor(authenticatedOptions(accessToken, signal));
  }

  public async login(
    request: LoginRequest,
    installationId: string,
    signal?: AbortSignal,
  ): Promise<TokenSetResponse> {
    this.ensureConfigured();
    return this.client.login(request, {
      headers: withInstallationId(installationId),
      signal,
    });
  }

  public async logoutSession(
    refreshToken: string,
    installationId: string,
    signal?: AbortSignal,
  ): Promise<void> {
    this.ensureConfigured();
    const request: LogoutRequest = { refreshToken };
    await this.client.logoutSession(request, {
      headers: withInstallationId(installationId),
      signal,
    });
  }

  public async refreshSession(
    refreshToken: string,
    installationId: string,
    signal?: AbortSignal,
  ): Promise<TokenSetResponse> {
    this.ensureConfigured();
    const request: RefreshRequest = { refreshToken };
    return this.client.refreshSession(request, {
      headers: withInstallationId(installationId),
      signal,
    });
  }

  public async listOpenServiceDates(
    accessToken: string,
    query: CursorQuery = {},
    signal?: AbortSignal,
  ): Promise<CursorPageOfServiceDateResponse> {
    this.ensureConfigured();
    return this.client.listOpenServiceDates(
      query,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async listThreadMessages(
    accessToken: string,
    dateId: string,
    query: CursorQuery = {},
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<ThreadMessagePageResponse>> {
    this.ensureConfigured();
    return this.client.listThreadMessages(
      dateId,
      query,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async postThreadMessage(
    accessToken: string,
    dateId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: PostThreadMessageRequest,
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<ThreadMessageResponse>> {
    this.ensureConfigured();
    return this.client.postThreadMessage(
      dateId,
      idempotencyKey,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async reportThreadMessage(
    accessToken: string,
    dateId: string,
    messageId: string,
    expectedVersion: number,
    body: ReportThreadMessageRequest,
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<void>> {
    this.ensureConfigured();
    return this.client.reportThreadMessage(
      dateId,
      messageId,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async hideThreadMessage(
    accessToken: string,
    dateId: string,
    messageId: string,
    expectedVersion: number,
    body: ReasonRequest,
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<ThreadMessageResponse>> {
    this.ensureConfigured();
    return this.client.hideThreadMessage(
      dateId,
      messageId,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async lockThread(
    accessToken: string,
    dateId: string,
    expectedVersion: number,
    body: ReasonRequest,
    signal?: AbortSignal,
  ): Promise<ApiSuccessEnvelope<ThreadStateResponse>> {
    this.ensureConfigured();
    return this.client.lockThread(
      dateId,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async readPrivilegedThreadMessages(
    accessToken: string,
    stepUpToken: string,
    body: PrivilegedThreadReadRequest,
    signal?: AbortSignal,
  ): Promise<PrivilegedThreadMessagePageResponse> {
    this.ensureConfigured();
    return this.client.readPrivilegedThreadMessages(
      stepUpToken,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async getServiceDate(
    accessToken: string,
    dateId: string,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse> {
    this.ensureConfigured();
    return this.client.getServiceDate(
      dateId,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async listEligibleSignupParticipants(
    accessToken: string,
    query: CursorQuery = {},
    signal?: AbortSignal,
  ): Promise<CursorPageOfEligibleSignupParticipantResponse> {
    this.ensureConfigured();
    return this.client.listEligibleSignupParticipants(
      query,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async submitSignup(
    accessToken: string,
    needId: string,
    idempotencyKey: string,
    body: SubmitSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse> {
    this.ensureConfigured();
    return this.client.submitSignup(
      needId,
      idempotencyKey,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async listMySignups(
    accessToken: string,
    query: CursorQuery = {},
    signal?: AbortSignal,
  ): Promise<CursorPageOfSignupResponse> {
    this.ensureConfigured();
    return this.client.listMySignups(
      query,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async getManagedRoster(
    accessToken: string,
    dateId: string,
    query: CursorQuery = {},
    signal?: AbortSignal,
  ): Promise<RosterResponse> {
    this.ensureConfigured();
    return this.client.getManagedRoster(
      dateId,
      query,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async openServiceDate(
    accessToken: string,
    dateId: string,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse> {
    this.ensureConfigured();
    return this.client.openServiceDate(
      dateId,
      idempotencyKey,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async editServiceDate(
    accessToken: string,
    dateId: string,
    expectedVersion: number,
    body: PatchServiceDateRequest,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse> {
    this.ensureConfigured();
    return this.client.editServiceDate(
      dateId,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async closeServiceDate(
    accessToken: string,
    dateId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: CloseServiceDateRequest,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse> {
    this.ensureConfigured();
    return this.client.closeServiceDate(
      dateId,
      idempotencyKey,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async cancelServiceDate(
    accessToken: string,
    dateId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: CancelServiceDateRequest,
    signal?: AbortSignal,
  ): Promise<ServiceDateResponse> {
    this.ensureConfigured();
    return this.client.cancelServiceDate(
      dateId,
      idempotencyKey,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async editHelpNeed(
    accessToken: string,
    needId: string,
    expectedVersion: number,
    body: PatchHelpNeedRequest,
    signal?: AbortSignal,
  ): Promise<HelpNeedResponse> {
    this.ensureConfigured();
    return this.client.editHelpNeed(
      needId,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async approveSignup(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: ApproveSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse> {
    this.ensureConfigured();
    return this.client.approveSignup(
      signupId,
      idempotencyKey,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async declineSignup(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: DeclineSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse> {
    this.ensureConfigured();
    return this.client.declineSignup(
      signupId,
      idempotencyKey,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async waitlistSignup(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: WaitlistSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse> {
    this.ensureConfigured();
    return this.client.waitlistSignup(
      signupId,
      idempotencyKey,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async withdrawSignup(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: WithdrawSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse> {
    this.ensureConfigured();
    return this.client.withdrawSignup(
      signupId,
      idempotencyKey,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async overrideSignupCancellation(
    accessToken: string,
    signupId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: OverrideSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse> {
    this.ensureConfigured();
    return this.client.overrideSignupCancellation(
      signupId,
      idempotencyKey,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  public async reassignWaitlistedSignup(
    accessToken: string,
    needId: string,
    idempotencyKey: string,
    expectedVersion: number,
    body: ReassignSignupRequest,
    signal?: AbortSignal,
  ): Promise<SignupResponse> {
    this.ensureConfigured();
    return this.client.reassignWaitlistedSignup(
      needId,
      idempotencyKey,
      expectedVersion,
      body,
      authenticatedOptions(accessToken, signal),
    );
  }

  private ensureConfigured(): void {
    if (!this.baseUrl) {
      throw new MobileApiConfigurationError();
    }
  }
}

function normalizeBaseUrl(baseUrl: string): string {
  const trimmed = baseUrl.trim();
  if (trimmed.length === 0) {
    return "";
  }

  return trimmed.replace(/\/api\/v1\/?$/i, "").replace(/\/+$/, "");
}

function toBearer(accessToken: string): string {
  return ["Bearer", accessToken].join(" ");
}

function withInstallationId(installationId: string): Record<string, string> {
  return {
    [installationIdHeader]: installationId,
  };
}

function authenticatedOptions(accessToken: string, signal?: AbortSignal) {
  return {
    headers: {
      authorization: toBearer(accessToken),
    },
    signal,
  };
}

let defaultMobileApi: GeneratedAuthApi | null = null;

export function getMobileApi(): GeneratedAuthApi {
  if (!defaultMobileApi) {
    defaultMobileApi = new GeneratedAuthApi(
      process.env.EXPO_PUBLIC_API_BASE_URL ?? "",
    );
  }

  return defaultMobileApi;
}
