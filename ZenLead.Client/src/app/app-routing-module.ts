import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { Register } from './features/auth/register/register';
import { Login } from './features/auth/login/login';
import { authGuard } from './core/auth/auth.guard';
import { Shell } from './core/layout/shell/shell';
import { ComingSoon } from './core/placeholder/coming-soon';

const routes: Routes = [
  { path: '', redirectTo: 'leads', pathMatch: 'full' },
  { path: 'login', component: Login },
  { path: 'register', component: Register },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: 'leads', loadChildren: () => import('./features/leads/leads-module').then(m => m.LeadsModule) },
      { path: 'campaigns', component: ComingSoon, data: { title: 'Campaigns', sprint: 3 } },
      { path: 'inbox', component: ComingSoon, data: { title: 'Inbox', sprint: 4 } },
      { path: 'analytics', component: ComingSoon, data: { title: 'Analytics', sprint: 5 } }
    ]
  },
  { path: '**', redirectTo: 'leads' }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})
export class AppRoutingModule { }
