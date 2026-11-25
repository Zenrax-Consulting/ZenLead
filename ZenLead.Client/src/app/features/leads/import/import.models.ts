export type ImportField =
  | 'name' | 'firstName' | 'lastName' | 'email' | 'title'
  | 'companyName' | 'companyDomain' | 'industry' | 'country' | 'companySize';

/** Lead field → CSV header (the file's own spelling). Unmapped fields are absent or null. */
export type ColumnMapping = Partial<Record<ImportField, string | null>>;

export interface UploadResponse {
  batchId: string;
  headers: string[];
  sampleRows: string[][];
  rowCount: number;
  suggestedMapping: ColumnMapping;
}

export type ImportStatus = 'Uploaded' | 'Parsing' | 'Completed' | 'Failed';

export interface ImportStatusResponse {
  id: string;
  fileName: string;
  status: ImportStatus;
  rowCount: number;
  processedRowCount: number;
  importedCount: number;
  skippedDuplicateCount: number;
  skippedSuppressedCount: number;
  errorCount: number;
  failureReason: string | null;
  percent: number;
}

export const IMPORT_FIELDS: { key: ImportField; label: string; required?: boolean }[] = [
  { key: 'email', label: 'Email', required: true },
  { key: 'name', label: 'Full name' },
  { key: 'firstName', label: 'First name' },
  { key: 'lastName', label: 'Last name' },
  { key: 'title', label: 'Job title' },
  { key: 'companyName', label: 'Company' },
  { key: 'companyDomain', label: 'Company domain / website' },
  { key: 'industry', label: 'Industry' },
  { key: 'country', label: 'Country' },
  { key: 'companySize', label: 'Company size' }
];

export const isTerminalImport = (s: ImportStatusResponse): boolean => s.status === 'Completed' || s.status === 'Failed';

/** Rows that were not imported and appear in the issues CSV. */
export const issueCount = (s: ImportStatusResponse): number =>
  s.skippedDuplicateCount + s.skippedSuppressedCount + s.errorCount;
