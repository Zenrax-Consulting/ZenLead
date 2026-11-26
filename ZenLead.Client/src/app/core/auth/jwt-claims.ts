export interface Claims {
  sub: string;
  email?: string;
  workspace_id?: string;
  role?: string;
  exp: number;
}

/** UI hints only (route choice, hiding menus). The server enforces everything; never treat this as a security boundary. */
export function decodeClaims(token: string | null): Claims | null {
  if (!token) return null;
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const json = decodeURIComponent(
      atob(payload).split('').map(c => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2)).join('')
    );
    const claims = JSON.parse(json);
    return claims && typeof claims === 'object' ? (claims as Claims) : null;
  } catch {
    return null;
  }
}
