export interface TokenOption { token: string; label: string; }

export const TOKENS: TokenOption[] = [
  { token: '{{firstName}}', label: 'First name' }, { token: '{{lastName}}', label: 'Last name' }, { token: '{{name}}', label: 'Full name' },
  { token: '{{company}}', label: 'Company' }, { token: '{{title}}', label: 'Job title' }
];

/** Same allow-list and pattern as the server's TemplateRenderer: names are case-insensitive, whitespace inside braces is ignored. */
const KNOWN = new Set(['firstname', 'lastname', 'name', 'company', 'title']);
const TOKEN_PATTERN = /\{\{\s*([A-Za-z]+)\s*(\|[^}]*)?\}\}/g;

/** Pure: where does the caret land and what is the new text when inserting at the current selection. */
export function insertAt(value: string, start: number, end: number, text: string): { value: string; caret: number } {
  return { value: value.slice(0, start) + text + value.slice(end), caret: start + text.length };
}

function distinct(names: string[]): string[] {
  const seen = new Set<string>();
  return names.filter(n => {
    const key = n.toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** Token names the server would reject; shown as inline warnings while typing. */
export function unknownTokens(template: string): string[] {
  return distinct([...(template ?? '').matchAll(TOKEN_PATTERN)].map(m => m[1]).filter(n => !KNOWN.has(n.toLowerCase())));
}

/** Known tokens used without a fallback (lowercased): a lead with no value for one of these is skipped. */
export function requiredTokens(template: string): string[] {
  return distinct([...(template ?? '').matchAll(TOKEN_PATTERN)]
    .filter(m => m[2] === undefined && KNOWN.has(m[1].toLowerCase()))
    .map(m => m[1].toLowerCase()));
}
