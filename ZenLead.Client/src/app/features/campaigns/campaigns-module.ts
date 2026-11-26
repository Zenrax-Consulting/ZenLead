import { NgModule } from '@angular/core';
import { DragDropModule } from '@angular/cdk/drag-drop';
import { TextFieldModule } from '@angular/cdk/text-field';
import { MatTabsModule } from '@angular/material/tabs';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDividerModule } from '@angular/material/divider';
import { SharedModule } from '../../shared/shared-module';
import { CampaignsRoutingModule } from './campaigns-routing-module';
import { CampaignsSharedModule } from './campaigns-shared-module';
import { CampaignList } from './campaign-list/campaign-list';
import { CampaignDetail } from './campaign-detail/campaign-detail';
import { StepsTab } from './steps-tab/steps-tab';
import { StepEditor } from './step-editor/step-editor';
import { EnrollmentTab } from './enrollment-tab/enrollment-tab';
import { SettingsTab } from './settings-tab/settings-tab';
import {
  ActivationConfirmDialog, ActivationProblemsDialog, CampaignConfirmDialog, NewCampaignDialog, SenderHelpDialog, TimeZoneDialog
} from './dialogs/dialogs';

/**
 * Lazy. The extra Material/CDK modules live here rather than in SharedModule: SharedModule is eager, so anything added there
 * grows the initial bundle (already near its budget). Dialog, snack bar and chips were already shared.
 */
@NgModule({
  declarations: [
    CampaignList, CampaignDetail, StepsTab, StepEditor, EnrollmentTab, SettingsTab,
    NewCampaignDialog, CampaignConfirmDialog, ActivationConfirmDialog, ActivationProblemsDialog, SenderHelpDialog, TimeZoneDialog
  ],
  imports: [
    SharedModule, CampaignsSharedModule, CampaignsRoutingModule,
    DragDropModule, TextFieldModule, MatTabsModule, MatSlideToggleModule, MatButtonToggleModule, MatDividerModule
  ]
})
export class CampaignsModule {}
