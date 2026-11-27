import { CampaignStatus } from '../campaigns/campaigns.models';

/** Mirrors ZenLead.Application.Dtos.Analytics (F28). Dates are `yyyy-MM-dd` local days in the workspace time zone. */
export interface AnalyticsTotals {
  sent: number;
  delivered: number;
  opened: number;
  bounced: number;
  contactedLeads: number;
  repliedLeads: number;
  /** Delivered / Sent; null when nothing was sent. */
  deliveredRate: number | null;
  /** Opened / Delivered (unique messages; approximate). */
  openRate: number | null;
  /** Replied leads / leads contacted (human replies only). */
  replyRate: number | null;
  /** Bounced / Sent. */
  bounceRate: number | null;
  opensApproximate: boolean;
}

export interface TrendPoint { day: string; sent: number; opened: number; replies: number; }

export interface StepBreakdown {
  stepId: string;
  order: number;
  subjectTemplate: string;
  sent: number;
  delivered: number;
  opened: number;
  bounced: number;
  replies: number;
}

export interface CampaignRow { campaignId: string; name: string; status: CampaignStatus; totals: AnalyticsTotals; }

export interface WorkspaceAnalytics {
  from: string;
  to: string;
  timeZone: string;
  totals: AnalyticsTotals;
  trend: TrendPoint[];
  campaigns: CampaignRow[];
}

export interface CampaignAnalytics {
  campaignId: string;
  name: string;
  status: CampaignStatus;
  from: string;
  to: string;
  timeZone: string;
  totals: AnalyticsTotals;
  steps: StepBreakdown[];
  trend: TrendPoint[];
}

export interface DateRangeParams { from: string; to: string; }
