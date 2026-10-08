export interface Criteria {
  jobTitles: string[];
  industries: string[];
  countries: string[];
  companySizeMin: number | null;
  companySizeMax: number | null;
  companyDomains: string[];
}

export interface TargetProfile {
  id: string;
  name: string;
  criteria: Criteria;
  sourceLeadId: string | null;
  createdAt: string;
}

export interface TargetProfileRequest {
  name: string;
  criteria: Criteria;
}

export interface SuggestedProfile {
  name: string;
  criteria: Criteria;
  sourceLeadId: string | null;
}

export type RunStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'CapReached';

export interface DiscoveryRun {
  id: string;
  targetProfileId: string | null;
  provider: string;
  status: RunStatus;
  requestedCount: number;
  foundCount: number;
  importedCount: number;
  skippedDuplicateCount: number;
  skippedSuppressedCount: number;
  noEmailCount: number;
  creditsUsed: number;
  failureReason: string | null;
  createdAt: string;
  finishedAt: string | null;
}

export interface Credits {
  used: number;
  cap: number;
  remaining: number;
  providerRemaining: number | null;
  maxLeadsPerRun: number;
}

export const emptyCriteria = (): Criteria => ({
  jobTitles: [], industries: [], countries: [], companySizeMin: null, companySizeMax: null, companyDomains: []
});

export const isTerminal = (run: Pick<DiscoveryRun, 'status'>): boolean =>
  run.status === 'Completed' || run.status === 'Failed' || run.status === 'CapReached';

/** One-line summary of what a profile filters on, for list chips. */
export function criteriaChips(c: Criteria): string[] {
  const chips = [
    ...c.jobTitles.map(t => `Title: ${t}`),
    ...c.industries.map(i => `Industry: ${i}`),
    ...c.countries.map(x => `Country: ${x}`),
    ...c.companyDomains.map(d => `Domain: ${d}`)
  ];
  if (c.companySizeMin !== null || c.companySizeMax !== null)
    chips.push(`Size: ${c.companySizeMin ?? 0}–${c.companySizeMax ?? '∞'}`);
  return chips;
}
