import { ChangeDetectorRef, Component, OnDestroy, OnInit } from '@angular/core';
import { AbstractControl, FormBuilder, FormGroup, ValidationErrors, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { Observable, Subscription, catchError, debounceTime, distinctUntilChanged, filter, of, switchMap } from 'rxjs';
import { AuthService } from '../../../core/auth/auth.service';
import { WorkspaceOption } from '../../../core/auth/auth.models';
import { WorkspacePickerService } from '../../../core/workspace-picker.service';

/** Valid only when an option from the list was picked (free text is rejected; the server rejects unknown ids too). */
export function requiredOption(control: AbstractControl): ValidationErrors | null {
  const v = control.value as unknown;
  return v !== null && typeof v === 'object' && typeof (v as WorkspaceOption).id === 'string' ? null : { requiredOption: true };
}

@Component({
  selector: 'app-register',
  standalone: false,
  templateUrl: './register.html',
  styleUrl: './register.css'
})
export class Register implements OnInit, OnDestroy {
  form: FormGroup;
  options: WorkspaceOption[] = [];

  submitting = false;
  errorMessage: string | null = null;

  private subscription: Subscription | null = null;

  constructor(
    fb: FormBuilder,
    private auth: AuthService,
    private workspaces: WorkspacePickerService,
    private router: Router,
    private cdr: ChangeDetectorRef
  ) {
    this.form = fb.group({
      workspace: [null as WorkspaceOption | string | null, requiredOption],
      displayName: ['', Validators.required],
      email: ['', [Validators.required, Validators.email]],
      password: ['', [Validators.required, Validators.minLength(8)]]
    });
  }

  get workspaceCtrl(): AbstractControl {
    return this.form.controls['workspace'];
  }

  ngOnInit(): void {
    this.subscription = this.suggestions(this.workspaceCtrl.valueChanges).subscribe(options => {
      this.options = options;
      this.cdr.markForCheck();
    });
  }

  ngOnDestroy(): void {
    this.subscription?.unsubscribe();
  }

  /** Typed text → debounced server search; picking an option (an object) never triggers a search. */
  suggestions(values: Observable<unknown>, debounceMs = 250): Observable<WorkspaceOption[]> {
    return values.pipe(
      debounceTime(debounceMs),
      filter((v): v is string => typeof v === 'string'),
      distinctUntilChanged(),
      switchMap(q => this.workspaces.search(q.trim()).pipe(catchError(() => of([] as WorkspaceOption[]))))
    );
  }

  displayWith(option: WorkspaceOption | string | null): string {
    return typeof option === 'string' ? option : option?.name ?? '';
  }

  static messageFor(err: { status?: number; error?: { errors?: Record<string, string[]> } }): string {
    if (err.status === 429) return 'Too many requests, try again later.';
    if (err.status === 409) return "That workspace isn't accepting registrations yet.";
    if (err.status === 400 && err.error?.errors) {
      const messages = Object.values(err.error.errors).flat();
      if (messages.length) return messages.join(' ');
    }
    return 'Registration failed. Please try again.';
  }

  submit(): void {
    if (this.form.invalid) return;
    this.submitting = true;
    this.errorMessage = null;

    const { workspace, displayName, email, password } = this.form.getRawValue() as {
      workspace: WorkspaceOption; displayName: string; email: string; password: string;
    };

    this.auth.register({ email, password, displayName, workspaceId: workspace.id }).subscribe({
      next: () => this.router.navigate(['/register/pending'], { queryParams: { w: workspace.name } }),
      error: err => {
        this.submitting = false;
        this.errorMessage = Register.messageFor(err);
        this.cdr.markForCheck(); // app is zoneless: async state changes must schedule a render
      }
    });
  }
}
