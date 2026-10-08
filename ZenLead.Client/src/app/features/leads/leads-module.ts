import { NgModule } from '@angular/core';
import { SharedModule } from '../../shared/shared-module';
import { LeadsRoutingModule } from './leads-routing-module';
import { LeadsList } from './leads-list/leads-list';
import { LeadDetail } from './lead-detail/lead-detail';
import { AddLeadDialog } from './add-lead-dialog/add-lead-dialog';
import { ConfirmDialog } from './confirm-dialog/confirm-dialog';

@NgModule({
  declarations: [LeadsList, LeadDetail, AddLeadDialog, ConfirmDialog],
  imports: [SharedModule, LeadsRoutingModule]
})
export class LeadsModule {}
