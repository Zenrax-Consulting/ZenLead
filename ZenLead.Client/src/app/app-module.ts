import { firstValueFrom } from 'rxjs';
import { HTTP_INTERCEPTORS, provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import { NgModule, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';

import { AppRoutingModule } from './app-routing-module';
import { App } from './app';
import { SharedModule } from './shared/shared-module';
import { AuthService } from './core/auth/auth.service';
import { AuthInterceptor } from './core/auth/auth.interceptor';
import { Shell } from './core/layout/shell/shell';
import { ComingSoon } from './core/placeholder/coming-soon';
import { Register } from './features/auth/register/register';
import { Login } from './features/auth/login/login';

@NgModule({
  declarations: [
    App,
    Register,
    Login,
    Shell,
    ComingSoon
  ],
  imports: [
    BrowserModule, SharedModule,
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
