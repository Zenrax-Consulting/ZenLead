import { guessMapping, normalizeHeader } from './column-guess';

describe('guessMapping', () => {
  it.each([
    ['E-mail Address', 'email'],
    ['Work Email', 'email'],
    ['EMAIL', 'email'],
    ['Job Title', 'title'],
    ['Position', 'title'],
    ['Company Name', 'companyName'],
    ['Organisation', 'companyName'],
    ['Website', 'companyDomain'],
    ['Company Domain', 'companyDomain'],
    ['# Employees', 'companySize'],
    ['Company Size', 'companySize'],
    ['Industry', 'industry'],
    ['Country', 'country'],
    ['Full Name', 'name']
  ])('maps "%s" to %s', (header, field) => {
    expect(guessMapping([header])).toEqual({ [field]: header });
  });

  it('maps First Name / Last Name / Surname', () => {
    expect(guessMapping(['First Name', 'Surname'])).toEqual({ firstName: 'First Name', lastName: 'Surname' });
  });

  it('is case, punctuation and whitespace insensitive', () => {
    expect(normalizeHeader('  First_Name ')).toBe('firstname');
    expect(guessMapping(['  first-NAME  '])).toEqual({ firstName: '  first-NAME  ' });
  });

  it('prefers First / Last over Name when both exist', () => {
    expect(guessMapping(['Name', 'First Name', 'Last Name', 'Email'])).toEqual({ email: 'Email', firstName: 'First Name', lastName: 'Last Name' });
  });

  it('uses synonyms in priority order', () => {
    // "email" outranks "workemail"; "companydomain" outranks "website"
    expect(guessMapping(['Work Email', 'Email', 'Website', 'Company Domain'])).toEqual({ email: 'Email', companyDomain: 'Company Domain' });
  });

  it('never assigns a header to two fields', () => {
    const result = guessMapping(['Company', 'Title', 'Email']);
    const values = Object.values(result);
    expect(new Set(values).size).toBe(values.length);
  });

  it('leaves unknown headers unmapped', () => {
    expect(guessMapping(['Favourite colour', 'Notes'])).toEqual({});
  });
});
