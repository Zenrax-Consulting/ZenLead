import { firstValueFrom } from 'rxjs';
import { HTTP_INTERCEPTORS, provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { NgModule, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { BrowserModule } from '@angular/platform-browser';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTableModule } from '@angular/material/table';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { AppRoutingModule } from './app-routing-module';
import { App } from './app';
import { AuthService } from './core/auth/auth.service';
import { AuthInterceptor } from './core/auth/auth.interceptor';
import { Register } from './features/auth/register/register';
import { Login } from './features/auth/login/login';
import { LeadsList } from './features/leads/leads-list/leads-list';
import { LeadDetail } from './features/leads/lead-detail/lead-detail';

@NgModule({
  declarations: [
    App,
    Register,
    Login,
    LeadsList,
    LeadDetail
  ],
  imports: [
    BrowserModule, ReactiveFormsModule,
    MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatTableModule, MatProgressSpinnerModule,
    AppRoutingModule
  ],
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptorsFromDi()),
    { provide: HTTP_INTERCEPTORS, useClass: AuthInterceptor, multi: true },
    provideAppInitializer(() => firstValueFrom(inject(AuthService).tryRestoreSession()))
  ],
  bootstrap: [App]
})
export class AppModule { }
