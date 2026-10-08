export type LeadStatus = 'New' | 'Contacted' | 'Replied' | 'Unsubscribed' | 'Bounced';
export type LeadSource = 'Manual' | 'Csv' | 'Discovery';
export type EmailVerificationStatus = 'Unverified' | 'Verified' | 'Risky' | 'Invalid';

export const LEAD_STATUSES: LeadStatus[] = ['New', 'Contacted', 'Replied', 'Unsubscribed', 'Bounced'];
export const LEAD_SOURCES: LeadSource[] = ['Manual', 'Csv', 'Discovery'];

export interface CompanySummary {
  id: string;
  name: string;
  domain: string | null;
  industry: string | null;
  country: string | null;
}

export interface Lead {
  id: string;
  name: string;
  email: string;
  title: string | null;
  status: LeadStatus;
  createdAt: string;
  company: CompanySummary | null;
  source: LeadSource;
  sourceRunId: string | null;
  emailVerificationStatus: EmailVerificationStatus;
}

export interface LeadQuery {
  page: number;
  pageSize: number;
  q?: string;
  status?: LeadStatus;
  companyId?: string;
  source?: LeadSource;
  sourceRunId?: string;
  sort?: string;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface CreateLeadRequest {
  name: string;
  email: string;
  title: string | null;
  companyName?: string | null;
  companyDomain?: string | null;
}

export interface UpdateLeadRequest {
  name: string;
  title: string | null;
  status: LeadStatus;
  companyName: string | null;
  companyDomain: string | null;
}

export interface ComposedEmail {
  subject: string;
  body: string;
  tokensUsed: number;
}
