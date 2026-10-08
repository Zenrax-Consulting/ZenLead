import { Injectable } from '@angular/core';
import { SuggestedProfile } from './profiles.models';

/**
 * Carries a suggested profile from "Create from lead(s)" to the editor. A root service rather than history.state,
 * which is lost on refresh and awkward with the zoneless router; a refresh simply drops the draft.
 */
@Injectable({ providedIn: 'root' })
export class ProfileDraftService {
  private draft: SuggestedProfile | null = null;

  set(draft: SuggestedProfile): void { this.draft = draft; }

  /** Returns the draft once, then forgets it. */
  take(): SuggestedProfile | null {
    const d = this.draft;
    this.draft = null;
    return d;
  }
}
