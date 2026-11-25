export interface WorkspaceAdmin {
  id: string;
  name: string;
  adminEmail: string | null;
  timeZone: string;
  userCount: number;
  createdAt: string;
}

export interface CreateWorkspaceRequest {
  name: string;
  adminEmail: string;
  timeZone?: string | null;
}
