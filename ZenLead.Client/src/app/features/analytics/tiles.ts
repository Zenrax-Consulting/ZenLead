import { AnalyticsTotals } from './analytics.models';
import { BOUNCE_WARN_RATE, formatCount, formatRate } from './format-rate';

export const OPENS_CAVEAT =
  'Open counts are approximate. Apple Mail Privacy Protection and image blocking mean some opens are over- or under-counted.';

export interface StatTileModel {
  key: 'sent' | 'delivered' | 'opened' | 'replied' | 'bounced';
  label: string;
  value: string;
  /** e.g. "97.0% of sent"; "—" with a reason in `info` when the rate has no denominator. */
  rateLine: string | null;
  /** Shown behind an info icon (tooltip + accessible label). */
  info: string | null;
  /** Written warning (never colour alone), e.g. a high bounce rate. */
  warning: string | null;
}

function rateLine(rate: number | null, suffix: string): string {
  return rate === null ? `— ${suffix}` : `${formatRate(rate)} ${suffix}`;
}

/** The five headline tiles. Opens are always marked approximate; null rates explain themselves. */
export function buildTiles(t: AnalyticsTotals): StatTileModel[] {
  const noSends = 'No emails were sent in this range.';
  return [
    { key: 'sent', label: 'Sent', value: formatCount(t.sent), rateLine: 'emails in this range', info: null, warning: null },
    {
      key: 'delivered', label: 'Delivered', value: formatCount(t.delivered), rateLine: rateLine(t.deliveredRate, 'of sent'),
      info: t.deliveredRate === null ? noSends : null, warning: null
    },
    {
      key: 'opened', label: 'Opened ≈', value: formatCount(t.opened), rateLine: rateLine(t.openRate, 'of delivered (approx.)'),
      info: t.openRate === null ? `${OPENS_CAVEAT} No emails were delivered in this range.` : OPENS_CAVEAT, warning: null
    },
    {
      key: 'replied', label: 'Replied', value: formatCount(t.repliedLeads), rateLine: rateLine(t.replyRate, 'of leads contacted'),
      info: t.replyRate === null ? `${noSends} Human replies only; auto-replies are excluded.` : 'Human replies only; auto-replies are excluded.',
      warning: null
    },
    {
      key: 'bounced', label: 'Bounced', value: formatCount(t.bounced), rateLine: rateLine(t.bounceRate, 'of sent'),
      info: t.bounceRate === null ? noSends : null,
      warning: t.bounceRate !== null && t.bounceRate >= BOUNCE_WARN_RATE ? 'High bounce rate (2% or more): check the lead list.' : null
    }
  ];
}
