import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LeadsList } from './leads-list/leads-list';
import { LeadDetail } from './lead-detail/lead-detail';

// Literal routes (/leads/import, /leads/profiles) must be declared before ':id' when they land.
const routes: Routes = [
  { path: '', component: LeadsList },
  { path: ':id', component: LeadDetail }
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class LeadsRoutingModule {}
