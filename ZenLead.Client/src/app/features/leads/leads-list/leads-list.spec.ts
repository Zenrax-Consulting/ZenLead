import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AppModule } from '../../../app-module';
import { LeadsList } from './leads-list';

// Regression: the app is zoneless, so async state changes only render if the component schedules a check.
// Before the fix the page sat on "Loading…" forever even though the API had answered.
describe('LeadsList rendering (zoneless)', () => {
  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [AppModule],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])]
    }).compileComponents();
  });

  it('replaces "Loading…" with the empty state once the API answers', async () => {
    const fixture = TestBed.createComponent(LeadsList);
    const http = TestBed.inject(HttpTestingController);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Loading…');

    http.expectOne('/api/v1/leads').flush([]);
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).not.toContain('Loading…');
    expect(fixture.nativeElement.textContent).toContain('No leads yet');
  });

  it('renders leads returned by the API', async () => {
    const fixture = TestBed.createComponent(LeadsList);
    const http = TestBed.inject(HttpTestingController);
    await fixture.whenStable();

    http.expectOne('/api/v1/leads').flush([
      { id: '1', name: 'Jane Doe', email: 'jane@example.com', title: 'CTO', status: 'New', createdAt: '2026-10-07T00:00:00Z' }
    ]);
    await fixture.whenStable();

    expect(fixture.nativeElement.textContent).toContain('Jane Doe');
  });
});
