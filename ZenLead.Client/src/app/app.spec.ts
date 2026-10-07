import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, RouterModule } from '@angular/router';
import { App } from './app';
import { AuthService } from './core/auth/auth.service';
import { AuthResponse } from './core/auth/auth.models';

describe('App', () => {
  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      declarations: [App],
      imports: [RouterModule.forRoot([])],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('hides the toolbar when signed out', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.app-toolbar')).toBeNull();
  });

  it('shows the toolbar when signed in, and logout clears the session and goes to /login', () => {
    const auth = TestBed.inject(AuthService);
    const response: AuthResponse = { accessToken: 'token', refreshToken: 'refresh' };
    auth['storeSession'](response); // private; tests set up a signed-in session directly
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.app-toolbar')).not.toBeNull();

    fixture.componentInstance.logout();

    expect(auth.isAuthenticated()).toBe(false);
    expect(navigate).toHaveBeenCalledWith(['/login']);
  });
});
