import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { SharedModule } from '../../shared/shared-module';
import { AdminWorkspaces } from './admin-workspaces/admin-workspaces';
import { CreateWorkspaceDialog } from './create-workspace-dialog/create-workspace-dialog';

const routes: Routes = [{ path: '', component: AdminWorkspaces }];

@NgModule({
  declarations: [AdminWorkspaces, CreateWorkspaceDialog],
  imports: [SharedModule, RouterModule.forChild(routes)]
})
export class AdminModule {}
