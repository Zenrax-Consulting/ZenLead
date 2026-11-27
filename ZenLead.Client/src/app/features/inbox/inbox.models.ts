import { LeadStatus } from '../leads/leads.models';

export type ReplyClassification = 'Unclassified' | 'Interested' | 'NotInterested' | 'Unsubscribe' | 'OutOfOffice' | 'Bounce';
export type MessageDirection = 'Inbound' | 'Outbound';
export type ClassificationSource = 'None' | 'Header' | 'Ai' | 'Manual';
export type OutboundDeliveryStatus = 'Pending' | 'Sent' | 'Failed';

/** The labels a human may set (the API rejects Unclassified and Bounce). */
export const MANUAL_LABELS: ReplyClassification[] = ['Interested', 'NotInterested', 'Unsubscribe', 'OutOfOffice'];

export interface ThreadListQuery {
  page: number;
  pageSize: number;
  classification?: ReplyClassification;
  unread?: boolean;
  needsReview?: boolean;
  campaignId?: string;
  q?: string;
}

export interface ThreadListItem {
  id: string;
  leadId: string;
  leadName: string;
  leadEmail: string;
  companyName: string | null;
  campaignId: string | null;
  campaignName: string | null;
  subject: string;
  snippet: string;
  lastMessageAt: string;
  isRead: boolean;
  classification: ReplyClassification;
  needsReview: boolean;
  messageCount: number;
}

export interface InboxMessage {
  id: string;
  direction: MessageDirection;
  fromAddress: string;
  fromName: string | null;
  subject: string;
  textBody: string;
  fullTextBody: string | null;
  occurredAt: string;
  classification: ReplyClassification;
  classificationConfidence: number | null;
  classifiedBy: ClassificationSource;
  needsReview: boolean;
  senderMismatch: boolean;
  isAutoReply: boolean;
  deliveryStatus: OutboundDeliveryStatus | null;
  deliveryError: string | null;
}

export interface ThreadDetail {
  id: string;
  leadId: string;
  leadName: string;
  leadEmail: string;
  leadTitle: string | null;
  companyName: string | null;
  leadStatus: LeadStatus;
  campaignId: string | null;
  campaignName: string | null;
  subject: string;
  classification: ReplyClassification;
  canReply: boolean;
  cannotReplyReason: string | null;
  messages: InboxMessage[];
}

export interface ReplyRequest { clientRequestId: string; body: string; }
export interface UnreadCount { count: number; }
