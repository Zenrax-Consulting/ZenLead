import { Injectable } from '@angular/core';
import { LeadQuery } from './leads.models';

/** The selection contract F14 (target profiles) and F24 (campaign enrollment) read. */
@Injectable({ providedIn: 'root' })
export class LeadSelectionService {
  private ids = new Set<string>();
  /** The filter the list was showing — lets F24 offer "all N matching this filter". */
  lastQuery: LeadQuery | null = null;
  lastTotal = 0;

  get count(): number { return this.ids.size; }
  get selected(): string[] { return [...this.ids]; }
  isSelected(id: string): boolean { return this.ids.has(id); }
  toggle(id: string): void { if (this.ids.has(id)) this.ids.delete(id); else this.ids.add(id); }
  setMany(ids: string[], selected: boolean): void { ids.forEach(id => selected ? this.ids.add(id) : this.ids.delete(id)); }
  clear(): void { this.ids.clear(); }
}
