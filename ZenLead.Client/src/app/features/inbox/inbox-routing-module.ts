import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { InboxPage } from './inbox-page/inbox-page';

/** One page for /inbox and /inbox/:threadId, so the list (and its scroll) survives selecting a thread. */
export const INBOX_ROUTES: Routes = [
  { path: '', component: InboxPage, children: [{ path: ':threadId', children: [] }] }
];

@NgModule({
  imports: [RouterModule.forChild(INBOX_ROUTES)],
  exports: [RouterModule]
})
export class InboxRoutingModule {}
