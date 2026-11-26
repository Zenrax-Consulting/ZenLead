import { decodeClaims } from './jwt-claims';

/** Builds an unsigned JWT-shaped string (header.payload.sig) with base64url encoding, like the API issues. */
function fakeJwt(payload: object): string {
  const b64url = (s: string) => btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  const utf8 = (o: object) => unescape(encodeURIComponent(JSON.stringify(o)));
  return `${b64url(utf8({ alg: 'HS256', typ: 'JWT' }))}.${b64url(utf8(payload))}.sig`;
}

describe('decodeClaims', () => {
  it('decodes a workspace user token', () => {
    const claims = decodeClaims(fakeJwt({ sub: 'u1', email: 'a@acme.com', workspace_id: 'w1', exp: 123 }));
    expect(claims).toEqual({ sub: 'u1', email: 'a@acme.com', workspace_id: 'w1', exp: 123 });
  });

  it('decodes the super admin role and no workspace', () => {
    const claims = decodeClaims(fakeJwt({ sub: 'u2', role: 'SuperAdmin', exp: 1 }));
    expect(claims?.role).toBe('SuperAdmin');
    expect(claims?.workspace_id).toBeUndefined();
  });

  it('handles base64url characters and non-ASCII text', () => {
    const claims = decodeClaims(fakeJwt({ sub: 'u3', email: 'jöhn+?>@acme.com', exp: 1 }));
    expect(claims?.email).toBe('jöhn+?>@acme.com');
  });

  it.each([null, '', 'not-a-jwt', 'a.!!!.c', `x.${btoa('not json')}.y`, `x.${btoa('42')}.y`])('returns null for %s', token => {
    expect(decodeClaims(token)).toBeNull();
  });
});
