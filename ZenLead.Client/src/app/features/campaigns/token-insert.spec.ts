import { insertAt, requiredTokens, unknownTokens } from './token-insert';

describe('token-insert', () => {
  it('inserts at the caret', () => {
    expect(insertAt('Hi there', 3, 3, '{{firstName}} ')).toEqual({ value: 'Hi {{firstName}} there', caret: 17 });
  });

  it('replaces a selection', () => {
    expect(insertAt('Hi NAME, hello', 3, 7, '{{firstName}}')).toEqual({ value: 'Hi {{firstName}}, hello', caret: 16 });
  });

  it('inserts at the start and at the end', () => {
    expect(insertAt('abc', 0, 0, 'X')).toEqual({ value: 'Xabc', caret: 1 });
    expect(insertAt('abc', 3, 3, 'X')).toEqual({ value: 'abcX', caret: 4 });
    expect(insertAt('', 0, 0, '{{company}}')).toEqual({ value: '{{company}}', caret: 11 });
  });

  it('finds unknown tokens', () => {
    expect(unknownTokens('Hi {{foo}} and {{ bar }} and {{foo}}')).toEqual(['foo', 'bar']);
  });

  it('ignores known tokens, with fallbacks, whatever their case', () => {
    expect(unknownTokens('Hi {{firstName|there}}, {{COMPANY}}, {{ lastname }}, {{Title|friend}}, {{name}}')).toEqual([]);
  });

  it('lists known tokens used without a fallback', () => {
    expect(requiredTokens('{{firstName|there}} at {{Company}} ({{title}}) {{foo}}')).toEqual(['company', 'title']);
    expect(requiredTokens('{{company|your team}}')).toEqual([]);
  });
});
