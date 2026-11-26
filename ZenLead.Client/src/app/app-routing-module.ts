import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { Register } from './features/auth/register/register';
import { Login } from './features/auth/login/login';
import { RegisterPending } from './features/auth/register-pending/register-pending';
import { Approve } from './features/auth/approve/approve';
import { ForgotPassword } from './features/auth/forgot-password/forgot-password';
import { ResetPassword } from './features/auth/reset-password/reset-password';
import { authGuard, superAdminGuard, tenantGuard } from './core/auth/auth.guard';
import { Unsubscribe } from './features/public/unsubscribe/unsubscribe';
import { Shell } from './core/layout/shell/shell';
import { ComingSoon } from './core/placeholder/coming-soon';

const routes: Routes = [
  { path: '', redirectTo: 'leads', pathMatch: 'full' },
  { path: 'login', component: Login },
  { path: 'register', component: Register },
  { path: 'register/pending', component: RegisterPending },
  { path: 'approve', component: Approve },          // public: the token in the link is the credential
  { path: 'forgot-password', component: ForgotPassword },
  { path: 'reset-password', component: ResetPassword },   // public: the token in the link is the credential
  { path: 'unsubscribe/:token', component: Unsubscribe },   // public (F23): the signed token is the credential; GET shows, the button POSTs
  {
    path: '',
    component: Shell,
    canActivate: [tenantGuard],
    children: [
      { path: 'leads', loadChildren: () => import('./features/leads/leads-module').then(m => m.LeadsModule) },
      { path: 'campaigns', loadChildren: () => import('./features/campaigns/campaigns-module').then(m => m.CampaignsModule) },
      { path: 'inbox', component: ComingSoon, data: { title: 'Inbox', sprint: 4 } },
      { path: 'analytics', component: ComingSoon, data: { title: 'Analytics', sprint: 5 } }
    ]
  },
  {
    path: 'admin',
    component: Shell,
    canActivate: [superAdminGuard],
    children: [
      { path: 'workspaces', loadChildren: () => import('./features/admin/admin-module').then(m => m.AdminModule) },
      { path: '', redirectTo: 'workspaces', pathMatch: 'full' }
    ]
  },
  {
    // both roles: workspace users and the super admin (F21)
    path: 'account',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: 'password', loadChildren: () => import('./features/account/account-module').then(m => m.AccountModule) }
    ]
  },
  { path: '**', redirectTo: 'leads' }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
