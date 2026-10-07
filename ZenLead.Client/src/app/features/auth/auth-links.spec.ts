import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AppModule } from '../../app-module';
import { Login } from './login/login';
import { Register } from './register/register';

describe('login/register cross-links', () => {
  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [AppModule],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])]
    }).compileComponents();
  });

  it('login page links to /register', async () => {
    const fixture = TestBed.createComponent(Login);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('a[href="/register"]')).not.toBeNull();
  });

  it('register page links to /login', async () => {
    const fixture = TestBed.createComponent(Register);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('a[href="/login"]')).not.toBeNull();
  });
});
