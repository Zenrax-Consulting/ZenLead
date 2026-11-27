import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { StatTileModel } from '../tiles';

/** One headline number: label, big value, a secondary rate line, an optional info icon and a written warning. */
@Component({
  selector: 'app-stat-tile',
  standalone: false,
  templateUrl: './stat-tile.html',
  styleUrl: './stat-tile.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class StatTile {
  @Input({ required: true }) tile!: StatTileModel;
}
