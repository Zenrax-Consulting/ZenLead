import { NgModule } from '@angular/core';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { SharedModule } from '../../shared/shared-module';
import { AnalyticsRoutingModule } from './analytics-routing-module';
import { AnalyticsDashboard } from './analytics-dashboard/analytics-dashboard';
import { CampaignAnalytics } from './campaign-analytics/campaign-analytics';
import { StatTile } from './stat-tile/stat-tile';
import { TrendChart } from './trend-chart/trend-chart';
import { BarList } from './bar-list/bar-list';
import { RangeBar } from './range-bar/range-bar';

/** Lazy (F28). Charts are hand-rolled SVG (no chart library); colours come from the --chart-* variables in styles.css. */
@NgModule({
  declarations: [AnalyticsDashboard, CampaignAnalytics, StatTile, TrendChart, BarList, RangeBar],
  imports: [SharedModule, AnalyticsRoutingModule, MatButtonToggleModule, MatDatepickerModule],
  providers: [provideNativeDateAdapter()]
})
export class AnalyticsModule {}
