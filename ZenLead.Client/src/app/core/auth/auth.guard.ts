import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  if (auth.isAuthenticated()) return true;

  inject(Router).navigate(['/login']);
  return false;
};

/** Workspace (tenant) screens. The super admin has no workspace and is sent to the admin area instead. */
export const tenantGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.isAuthenticated()) return router.parseUrl('/login');
  return auth.isSuperAdmin() ? router.parseUrl('/admin/workspaces') : true;
};

/** Platform admin screens. Everyone else is sent to their workspace. */
export const superAdminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.isAuthenticated()) return router.parseUrl('/login');
  return auth.isSuperAdmin() ? true : router.parseUrl('/leads');
};
