import { ColumnMapping, ImportField } from './import.models';

// Client-side port of ZenLead.Application/Csv/ColumnGuesser.cs — keep the two tables identical.
// Synonyms are normalised (lower-case, letters and digits only) and listed in priority order.
// The server's suggestion is what the wizard shows first; this only runs when the user clicks "Auto-detect".
const SYNONYMS: Record<ImportField, string[]> = {
  email: ['email', 'emailaddress', 'workemail'],
  firstName: ['firstname', 'givenname'],
  lastName: ['lastname', 'surname', 'familyname'],
  name: ['name', 'fullname'],
  title: ['title', 'jobtitle', 'position'],
  companyName: ['company', 'companyname', 'organization', 'organisation'],
  companyDomain: ['companydomain', 'domain', 'website', 'url'],
  industry: ['industry'],
  country: ['country'],
  companySize: ['companysize', 'size', 'employees']
};

// Same order as the server: email, first, last, (name only if neither first nor last), then the rest.
const ORDER: ImportField[] = ['email', 'firstName', 'lastName', 'name', 'title', 'companyName', 'companyDomain', 'industry', 'country', 'companySize'];

export const normalizeHeader = (header: string): string => header.toLowerCase().replace(/[^\p{L}\p{N}]/gu, '');

/** A header is used for at most one field; Name is only guessed when neither First nor Last name was found. */
export function guessMapping(headers: string[]): ColumnMapping {
  const used = new Set<string>();
  const find = (field: ImportField): string | null => {
    for (const synonym of SYNONYMS[field]) {
      const hit = headers.find(h => !used.has(h) && normalizeHeader(h) === synonym);
      if (hit !== undefined) { used.add(hit); return hit; }
    }
    return null;
  };

  const result: ColumnMapping = {};
  for (const field of ORDER) {
    if (field === 'name' && (result.firstName || result.lastName)) continue;
    const hit = find(field);
    if (hit !== null) result[field] = hit;
  }
  return result;
}
