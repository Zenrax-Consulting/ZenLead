export type LeadStatus = 'New' | 'Contacted' | 'Replied' | 'Unsubscribed';

export interface Lead {
  id: string;
  name: string;
  email: string;
  title: string | null;
  status: LeadStatus;
  createdAt: string;
}

export interface CreateLeadRequest {
  name: string;
  email: string;
  title: string | null;
}
