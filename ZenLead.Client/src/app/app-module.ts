import { firstValueFrom } from 'rxjs';
import { HTTP_INTERCEPTORS, provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { NgModule, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';
import { MatBadgeModule } from '@angular/material/badge';

import { AppRoutingModule } from './app-routing-module';
import { App } from './app';
import { SharedModule } from './shared/shared-module';
import { AuthService } from './core/auth/auth.service';
import { AuthInterceptor } from './core/auth/auth.interceptor';
import { Shell } from './core/layout/shell/shell';
import { ComingSoon } from './core/placeholder/coming-soon';
import { Register } from './features/auth/register/register';
import { Login } from './features/auth/login/login';
import { RegisterPending } from './features/auth/register-pending/register-pending';
import { Approve } from './features/auth/approve/approve';
import { ForgotPassword } from './features/auth/forgot-password/forgot-password';
import { ResetPassword } from './features/auth/reset-password/reset-password';
import { Unsubscribe } from './features/public/unsubscribe/unsubscribe';
import { AiBudgetBanner } from './core/layout/ai-budget-banner/ai-budget-banner';

@NgModule({
  declarations: [
    App,
    Register,
    Login,
    RegisterPending,
    Approve,
    ForgotPassword,
    ResetPassword,
    Unsubscribe,
    Shell,
    AiBudgetBanner,
    ComingSoon
  ],
  imports: [
    BrowserModule, SharedModule, MatBadgeModule,
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
