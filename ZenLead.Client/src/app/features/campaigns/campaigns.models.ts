import { LeadQuery } from '../leads/leads.models';

export type CampaignStatus = 'Draft' | 'Active' | 'Paused' | 'Completed';
export type EnrollmentStatus = 'Active' | 'Paused' | 'Completed' | 'Replied' | 'Unsubscribed' | 'Bounced' | 'Failed';
export const ENROLLMENT_STATUSES: EnrollmentStatus[] = ['Active', 'Paused', 'Completed', 'Replied', 'Unsubscribed', 'Bounced', 'Failed'];

/** Day bit flags, as the server's [Flags] SendDays: Mon = 1 … Sun = 64. */
export const SendDays = { None: 0, Mon: 1, Tue: 2, Wed: 4, Thu: 8, Fri: 16, Sat: 32, Sun: 64, Weekdays: 31, All: 127 } as const;
export const DAY_NAMES = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const;

/** Flags → the set of day indexes (0 = Mon … 6 = Sun). */
export function daysToSet(days: number): Set<number> {
  const set = new Set<number>();
  DAY_NAMES.forEach((_, i) => { if (days & (1 << i)) set.add(i); });
  return set;
}

/** Day indexes (0 = Mon … 6 = Sun) → flags. */
export function setToDays(set: Iterable<number>): number {
  let days = 0;
  for (const i of set) if (i >= 0 && i < 7) days |= 1 << i;
  return days;
}

/** The API serialises enums as strings, so [Flags] SendDays arrives as "Weekdays", "All" or "Mon, Wed". It accepts numbers on the way in. */
export function parseSendDays(value: string | number | null | undefined): number {
  if (typeof value === 'number') return value;
  if (!value) return 0;
  const names = SendDays as Record<string, number>;
  return value.split(',').map(p => p.trim()).reduce((acc, name) => acc | (names[name] ?? 0), 0);
}

export interface Step {
  id: string;
  order: number;
  delayDays: number;
  subjectTemplate: string;
  bodyTemplate: string;
  useAiPersonalisation: boolean;
}

export interface StepRequest {
  delayDays: number;
  subjectTemplate: string;
  bodyTemplate: string;
  useAiPersonalisation: boolean;
}

export interface EnrollmentCounts {
  total: number;
  active: number;          // live: Active + Paused
  completed: number;
  replied: number;
  unsubscribed: number;
  bounced: number;
  failed: number;
}

export interface Campaign {
  id: string;
  name: string;
  status: CampaignStatus;
  fromName: string | null;
  dailySendCap: number;
  windowStart: string;     // "HH:mm" in effectiveTimeZone
  windowEnd: string;
  sendDays: number;        // flags (normalised by CampaignsService)
  timeZoneId: string | null;
  effectiveTimeZone: string;
  workspaceTimeZone: string;
  createdAt: string;
  activatedAt: string | null;
  steps: Step[];
  counts: EnrollmentCounts;
  /** How many steps some enrollment has already passed: those are pinned (no delete/reorder). */
  stepsStarted: number;
}

export interface CampaignRequest {
  name: string;
  fromName: string | null;
  dailySendCap: number;
  windowStart: string;
  windowEnd: string;
  sendDays: number;
  timeZoneId: string | null;
}

export function defaultCampaignRequest(name: string): CampaignRequest {
  return { name, fromName: null, dailySendCap: 30, windowStart: '09:00', windowEnd: '17:00', sendDays: SendDays.Weekdays, timeZoneId: null };
}

export interface CampaignListItem {
  id: string;
  name: string;
  status: CampaignStatus;
  stepCount: number;
  counts: EnrollmentCounts;
  createdAt: string;
  sent: number;
}

export interface EnrollResult {
  enrolled: number;
  alreadyEnrolled: number;
  skippedSuppressed: number;
  skippedInvalidEmail: number;
  skippedMissingTokens: number;
  notFound: number;
  warnUnverified: number;
  warnRisky: number;
  truncated: boolean;
}

export interface EnrollmentRow {
  id: string;
  leadId: string;
  leadName: string;
  leadEmail: string;
  nextStepOrder: number;
  nextSendAt: string | null;
  status: EnrollmentStatus;
  failureReason: string | null;
}

/** Codes: no_steps, steps_unordered, first_delay, empty_subject, empty_body, bad_token, window_invalid, domain_unverified. */
export interface ActivationProblem {
  code: string;
  message: string;
  stepOrder?: number | null;
}

/** 409 body from the campaign API. */
export interface CampaignConflict {
  code: string;
  message: string;
  problems: ActivationProblem[];
}

export interface ActivationPreview {
  enrolledActive: number;
  aiSteps: number;
  estimatedAiCalls: number;
  estimatedAiCostUsd: number;
  aiBudgetRemainingUsd: number;
}

export interface SenderStatus {
  fromAddress: string;
  fromName: string;
  domainVerified: boolean;
  inboundDomain: string;
}

export interface PreviewResponse {
  subject: string;
  body: string;
  missingTokens: string[];
}

/** Who to enroll: exactly one of explicit ids or the leads-list filter (the server rejects both/neither). */
export type EnrollTarget = { leadIds: string[]; filter?: undefined } | { filter: LeadQuery; leadIds?: undefined };
