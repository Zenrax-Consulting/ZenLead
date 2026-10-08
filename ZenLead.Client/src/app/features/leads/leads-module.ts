import { NgModule } from '@angular/core';
import { SharedModule } from '../../shared/shared-module';
import { LeadsRoutingModule } from './leads-routing-module';
import { LeadsList } from './leads-list/leads-list';
import { LeadDetail } from './lead-detail/lead-detail';

@NgModule({
  declarations: [LeadsList, LeadDetail],
  imports: [SharedModule, LeadsRoutingModule]
})
export class LeadsModule {}
