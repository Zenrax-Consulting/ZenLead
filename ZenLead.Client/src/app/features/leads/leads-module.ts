import { NgModule } from '@angular/core';
import { MatStepperModule } from '@angular/material/stepper';
import { SharedModule } from '../../shared/shared-module';
import { LeadsRoutingModule } from './leads-routing-module';
import { LeadsList } from './leads-list/leads-list';
import { LeadDetail } from './lead-detail/lead-detail';
import { AddLeadDialog } from './add-lead-dialog/add-lead-dialog';
import { ConfirmDialog } from './confirm-dialog/confirm-dialog';
import { TargetProfilesList } from './profiles/target-profiles-list/target-profiles-list';
import { TargetProfileEdit } from './profiles/target-profile-edit/target-profile-edit';
import { RunDiscoveryDialog } from './profiles/run-discovery-dialog/run-discovery-dialog';
import { ImportWizard } from './import/import-wizard';

@NgModule({
  declarations: [LeadsList, LeadDetail, AddLeadDialog, ConfirmDialog, TargetProfilesList, TargetProfileEdit, RunDiscoveryDialog, ImportWizard],
  // MatStepperModule stays here, not in SharedModule: SharedModule is eager, so anything added there grows the initial bundle.
  imports: [SharedModule, MatStepperModule, LeadsRoutingModule]
})
export class LeadsModule {}
