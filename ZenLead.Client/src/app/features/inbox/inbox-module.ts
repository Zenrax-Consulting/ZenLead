import { NgModule } from '@angular/core';
import { TextFieldModule } from '@angular/cdk/text-field';
import { SharedModule } from '../../shared/shared-module';
import { InboxRoutingModule } from './inbox-routing-module';
import { InboxPage } from './inbox-page/inbox-page';
import { Conversation, UnsubscribeConfirmDialog } from './conversation/conversation';
import { ReplyComposer } from './reply-composer/reply-composer';

/** Lazy (F27). Bodies are rendered as text only: nothing in this module binds [innerHTML]. */
@NgModule({
  declarations: [InboxPage, Conversation, ReplyComposer, UnsubscribeConfirmDialog],
  imports: [SharedModule, InboxRoutingModule, TextFieldModule]
})
export class InboxModule {}
