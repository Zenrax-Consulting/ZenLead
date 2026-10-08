import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LeadsList } from './leads-list/leads-list';
import { LeadDetail } from './lead-detail/lead-detail';
import { TargetProfilesList } from './profiles/target-profiles-list/target-profiles-list';
import { TargetProfileEdit } from './profiles/target-profile-edit/target-profile-edit';

// Literal routes (/leads/import, /leads/profiles) must be declared before ':id' when they land.
const routes: Routes = [
  { path: '', component: LeadsList },
  { path: 'profiles', component: TargetProfilesList },
  { path: 'profiles/new', component: TargetProfileEdit },
  { path: 'profiles/:id', component: TargetProfileEdit },
  { path: ':id', component: LeadDetail }
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class LeadsRoutingModule {}
