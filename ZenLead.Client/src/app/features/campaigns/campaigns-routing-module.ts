import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { CampaignList } from './campaign-list/campaign-list';
import { CampaignDetail } from './campaign-detail/campaign-detail';

const routes: Routes = [
  { path: '', component: CampaignList },
  { path: ':id', component: CampaignDetail }      // tabs: Steps | Leads | Settings (query param ?tab=)
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class CampaignsRoutingModule {}
