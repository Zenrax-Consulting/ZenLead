import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { SharedModule } from '../../shared/shared-module';
import { ChangePassword } from './change-password/change-password';

const routes: Routes = [{ path: '', component: ChangePassword }];

/** Signed-in account settings, for workspace users and the super admin alike (F21). */
@NgModule({
  declarations: [ChangePassword],
  imports: [SharedModule, RouterModule.forChild(routes)]
})
export class AccountModule {}
