import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ImportService } from './import.service';

describe('ImportService', () => {
  let service: ImportService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ImportService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('uploads the file as FormData with no explicit Content-Type', () => {
    const file = new File(['email\na@b.com'], 'leads.csv', { type: 'text/csv' });
    service.upload(file).subscribe();

    const req = http.expectOne('/api/v1/leads/import');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeInstanceOf(FormData);
    expect((req.request.body as FormData).get('file')).toBeInstanceOf(File);
    expect(req.request.headers.has('Content-Type')).toBe(false);
  });

  it('posts the mapping to start and reads status', () => {
    service.start('b1', { email: 'Email' }).subscribe();
    const start = http.expectOne('/api/v1/leads/import/b1/start');
    expect(start.request.method).toBe('POST');
    expect(start.request.body).toEqual({ email: 'Email' });

    service.status('b1').subscribe();
    expect(http.expectOne('/api/v1/leads/import/b1/status').request.method).toBe('GET');
  });

  it('downloads the issues file as a blob', () => {
    service.downloadErrors('b1').subscribe();
    const req = http.expectOne('/api/v1/leads/import/b1/errors');
    expect(req.request.responseType).toBe('blob');
  });
});
