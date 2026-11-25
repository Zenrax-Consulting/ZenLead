import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ColumnMapping, ImportStatusResponse, UploadResponse } from './import.models';

const BASE = '/api/v1/leads/import';

@Injectable({ providedIn: 'root' })
export class ImportService {
  constructor(private http: HttpClient) {}

  /** No manual Content-Type: the browser sets the multipart boundary. */
  upload(file: File): Observable<UploadResponse> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<UploadResponse>(BASE, form);
  }

  start(batchId: string, mapping: ColumnMapping): Observable<void> {
    return this.http.post<void>(`${BASE}/${batchId}/start`, mapping);
  }

  status(batchId: string): Observable<ImportStatusResponse> {
    return this.http.get<ImportStatusResponse>(`${BASE}/${batchId}/status`);
  }

  /** Authenticated download: a plain <a href> would not carry the in-memory bearer token. */
  downloadErrors(batchId: string): Observable<Blob> {
    return this.http.get(`${BASE}/${batchId}/errors`, { responseType: 'blob' });
  }
}
