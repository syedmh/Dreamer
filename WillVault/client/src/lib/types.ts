export enum VaultItemType {
  TextNote = 0,
  VoiceNote = 1,
  Video = 2,
  Document = 3,
  Will = 4,
}

export const VaultItemTypeLabels: Record<VaultItemType, string> = {
  [VaultItemType.TextNote]: "Text Note",
  [VaultItemType.VoiceNote]: "Voice Note",
  [VaultItemType.Video]: "Video",
  [VaultItemType.Document]: "Document",
  [VaultItemType.Will]: "Will",
};

export enum AccountStatus {
  Active = 0,
  VerificationPending = 1,
  Verified = 2,
  ReleaseScheduled = 3,
  Releasing = 4,
  Closed = 5,
}

export interface User {
  id: string;
  fullName: string;
  email: string;
  phone?: string;
  accountStatus: AccountStatus;
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  user: User;
}

export interface RegisterRequest {
  fullName: string;
  email: string;
  password: string;
  phone?: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface VaultItem {
  id: string;
  ownerId: string;
  title: string;
  description?: string;
  itemType: VaultItemType;
  originalFileName?: string;
  contentType?: string;
  fileSizeBytes?: number;
  isArchived: boolean;
  executorId?: string;
  legalNotes?: string;
  specialInstructions?: string;
  recipientCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface Recipient {
  id: string;
  ownerId: string;
  fullName: string;
  email: string;
  phone?: string;
  relationship: string;
  isEmailVerified: boolean;
  isPhoneVerified: boolean;
  emailVerifiedAt?: string;
  phoneVerifiedAt?: string;
  createdAt: string;
  updatedAt: string;
}

export interface VaultItemDetail extends VaultItem {
  recipients: Recipient[];
}

export interface TrustedContact {
  id: string;
  ownerId: string;
  fullName: string;
  email: string;
  phone?: string;
  relationship: string;
  isVerified: boolean;
  verifiedAt?: string;
  createdAt: string;
  updatedAt: string;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}
