import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { AnalyticsDashboard } from './analytics-dashboard/analytics-dashboard';
import { CampaignAnalytics } from './campaign-analytics/campaign-analytics';

export const ANALYTICS_ROUTES: Routes = [
  { path: '', component: AnalyticsDashboard, title: 'Analytics · ZenLead' },
  { path: 'campaigns/:id', component: CampaignAnalytics, title: 'Campaign analytics · ZenLead' }
];

@NgModule({
  imports: [RouterModule.forChild(ANALYTICS_ROUTES)],
  exports: [RouterModule]
})
export class AnalyticsRoutingModule {}
