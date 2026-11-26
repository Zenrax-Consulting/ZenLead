import { NgModule } from '@angular/core';
import { SharedModule } from '../../shared/shared-module';
import { EnrollDialog } from './enroll-dialog/enroll-dialog';
import { CampaignPickerDialog } from './campaign-picker-dialog/campaign-picker-dialog';

/**
 * The pieces of the campaigns feature the leads list opens (enroll-from-selection). Imported by both lazy modules — LeadsModule and
 * CampaignsModule — so it never lands in the eager bundle.
 */
@NgModule({
  declarations: [EnrollDialog, CampaignPickerDialog],
  imports: [SharedModule],
  exports: [EnrollDialog, CampaignPickerDialog]
})
export class CampaignsSharedModule {}
