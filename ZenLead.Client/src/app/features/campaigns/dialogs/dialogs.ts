import { Component, Inject } from '@angular/core';
import { FormControl, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { ActivationPreview, ActivationProblem, SenderStatus } from '../campaigns.models';
import { formatUsd } from '../format';

// Small dialogs of the campaigns feature. Material dialogs trap focus and restore it on close.

@Component({
  selector: 'app-new-campaign-dialog',
  standalone: false,
  template: `
    <h2 mat-dialog-title>New campaign</h2>
    <form (ngSubmit)="create()">
      <mat-dialog-content>
        <mat-form-field appearance="outline" class="full">
          <mat-label>Campaign name</mat-label>
          <input matInput [formControl]="name" maxlength="200" cdkFocusInitial>
        </mat-form-field>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button type="button" mat-dialog-close>Cancel</button>
        <button mat-raised-button color="primary" type="submit" [disabled]="name.invalid">Create</button>
      </mat-dialog-actions>
    </form>`,
  styles: ['.full { width: 100%; }']
})
export class NewCampaignDialog {
  name = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/\S/)] });
  constructor(private ref: MatDialogRef<NewCampaignDialog, string>) {}
  create(): void { if (this.name.valid) this.ref.close(this.name.value.trim()); }
}

export interface ConfirmData { title: string; message: string; confirmLabel: string; }

@Component({
  selector: 'app-campaign-confirm-dialog',
  standalone: false,
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>{{ data.message }}</mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="false">Cancel</button>
      <button mat-raised-button color="warn" [mat-dialog-close]="true">{{ data.confirmLabel }}</button>
    </mat-dialog-actions>`
})
export class CampaignConfirmDialog {
  constructor(@Inject(MAT_DIALOG_DATA) public data: ConfirmData) {}
}

export interface ActivationConfirmData {
  campaignName: string;
  preview: ActivationPreview;
  timeline: string;
  window: string;
}

@Component({
  selector: 'app-activation-confirm-dialog',
  standalone: false,
  template: `
    <h2 mat-dialog-title>Activate {{ data.campaignName }}?</h2>
    <mat-dialog-content>
      <p><strong>{{ data.preview.enrolledActive }}</strong> lead{{ data.preview.enrolledActive === 1 ? '' : 's' }} enrolled.</p>
      <p>Steps: {{ data.timeline }}</p>
      <p>Sends {{ data.window }}. Delays are calendar days; each email goes out inside the window on an allowed day.</p>
      <p *ngIf="data.preview.aiSteps > 0" class="ai-estimate">
        <mat-icon aria-hidden="true">auto_awesome</mat-icon>
        This will make ~{{ data.preview.estimatedAiCalls }} AI calls, about {{ usd(data.preview.estimatedAiCostUsd) }}
        (budget left {{ usd(data.preview.aiBudgetRemainingUsd) }}).
      </p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="false">Cancel</button>
      <button mat-raised-button color="primary" [mat-dialog-close]="true">Activate</button>
    </mat-dialog-actions>`,
  styles: ['.ai-estimate { display: flex; gap: 6px; align-items: center; }']
})
export class ActivationConfirmDialog {
  readonly usd = formatUsd;
  constructor(@Inject(MAT_DIALOG_DATA) public data: ActivationConfirmData) {}
}

export type ProblemsResult = { action: 'fix'; stepOrder: number } | { action: 'sender-help' };

@Component({
  selector: 'app-activation-problems-dialog',
  standalone: false,
  template: `
    <h2 mat-dialog-title>This campaign can't be activated yet</h2>
    <mat-dialog-content>
      <ul class="problems">
        <li *ngFor="let p of data">
          <mat-icon aria-hidden="true">error_outline</mat-icon>
          <span class="message">{{ p.message }}</span>
          <button mat-button *ngIf="p.code === 'domain_unverified'" (click)="senderHelp()">How to fix</button>
          <button mat-button *ngIf="p.code !== 'domain_unverified' && stepOf(p) as order" (click)="fix(order)">Fix</button>
        </li>
      </ul>
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Close</button></mat-dialog-actions>`,
  styles: ['.problems { list-style: none; padding: 0; } .problems li { display: flex; gap: 8px; align-items: center; } .message { flex: 1; }']
})
export class ActivationProblemsDialog {
  constructor(@Inject(MAT_DIALOG_DATA) public data: ActivationProblem[], private ref: MatDialogRef<ActivationProblemsDialog, ProblemsResult>) {}

  /** The step a problem is about: the server's stepOrder, else "Step N" in the message; no_steps points at the (empty) Steps tab. */
  stepOf(p: ActivationProblem): number | null {
    if (p.stepOrder) return p.stepOrder;
    const m = /Step (\d+)/.exec(p.message);
    if (m) return Number(m[1]);
    return p.code === 'no_steps' || p.code === 'steps_unordered' ? 1 : null;
  }

  fix(stepOrder: number): void { this.ref.close({ action: 'fix', stepOrder }); }
  senderHelp(): void { this.ref.close({ action: 'sender-help' }); }
}

@Component({
  selector: 'app-sender-help-dialog',
  standalone: false,
  template: `
    <h2 mat-dialog-title>Verify the sending domain</h2>
    <mat-dialog-content>
      <p>Campaign emails are sent from <strong>{{ data?.fromAddress ?? 'the configured address' }}</strong>.
         They stay blocked until the domain is verified with SendGrid:</p>
      <ol>
        <li>In SendGrid → Sender Authentication, authenticate the domain <code>leads.&lt;your domain&gt;</code> and add the DNS records it lists (CNAMEs for DKIM and the return path).</li>
        <li>Wait for DNS to propagate, then press <em>Verify</em> in SendGrid until every record shows verified.</li>
        <li>Set <code>Email:DomainVerified</code> to <code>true</code> in the app configuration and restart the API.</li>
      </ol>
      <p>Replies come back through <code>{{ data?.inboundDomain ?? 'the inbound domain' }}</code>: it needs an MX record pointing to <code>mx.sendgrid.net</code>.</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Close</button></mat-dialog-actions>`
})
export class SenderHelpDialog {
  constructor(@Inject(MAT_DIALOG_DATA) public data: SenderStatus | null) {}
}

@Component({
  selector: 'app-time-zone-dialog',
  standalone: false,
  template: `
    <h2 mat-dialog-title>Workspace time zone</h2>
    <mat-dialog-content>
      <p>Campaigns without their own zone send in this one.</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>Time zone</mat-label>
        <mat-select [formControl]="zone">
          <mat-option *ngFor="let z of data.zones" [value]="z">{{ z }}</mat-option>
        </mat-select>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-raised-button color="primary" [disabled]="zone.invalid" (click)="ref.close(zone.value)">Save</button>
    </mat-dialog-actions>`,
  styles: ['.full { width: 100%; }']
})
export class TimeZoneDialog {
  zone: FormControl<string>;
  constructor(@Inject(MAT_DIALOG_DATA) public data: { current: string; zones: string[] }, public ref: MatDialogRef<TimeZoneDialog, string>) {
    this.zone = new FormControl(data.current, { nonNullable: true, validators: [Validators.required] });
  }
}
