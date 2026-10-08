import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatSortModule } from '@angular/material/sort';
import { MatSelectModule } from '@angular/material/select';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialogModule } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';

const MATERIAL = [
  MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, MatTableModule,
  MatProgressSpinnerModule, MatSidenavModule, MatToolbarModule, MatListModule, MatIconModule, MatMenuModule,
  MatPaginatorModule, MatSortModule, MatSelectModule, MatAutocompleteModule, MatCheckboxModule,
  MatChipsModule, MatDialogModule, MatProgressBarModule, MatTooltipModule
];

@NgModule({
  imports: [CommonModule, ReactiveFormsModule, RouterModule, ...MATERIAL],
  exports: [CommonModule, ReactiveFormsModule, RouterModule, ...MATERIAL]
})
export class SharedModule {}
