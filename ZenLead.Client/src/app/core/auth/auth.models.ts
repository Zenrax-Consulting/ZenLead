export interface RegisterRequest {
  email: string;
  password: string;
  displayName: string;
  workspaceId: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
}

/** What the anonymous registration picker sees about a workspace. */
export interface WorkspaceOption {
  id: string;
  name: string;
}

/** `status` is pending | used | expired; requester details are present only while pending. */
export interface ApprovalInfo {
  status: 'pending' | 'used' | 'expired';
  requesterName: string | null;
  requesterEmail: string | null;
  workspaceName: string | null;
}
