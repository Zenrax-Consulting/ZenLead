import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { InboxService } from './inbox.service';

describe('InboxService', () => {
  let service: InboxService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(InboxService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('threads sends only the filters that are set', () => {
    service.threads({ page: 2, pageSize: 25, classification: 'Interested', campaignId: 'c1', q: 'acme' }).subscribe();
    const req = http.expectOne(r => r.url === '/api/v1/inbox/threads');
    expect(req.request.params.toString()).toBe('page=2&pageSize=25&classification=Interested&campaignId=c1&q=acme');
    req.flush({ items: [], total: 0, page: 2, pageSize: 25 });

    service.threads({ page: 1, pageSize: 25, unread: true, needsReview: false }).subscribe();
    const req2 = http.expectOne(r => r.url === '/api/v1/inbox/threads');
    expect(req2.request.params.toString()).toBe('page=1&pageSize=25&unread=true&needsReview=false');
    req2.flush({ items: [], total: 0, page: 1, pageSize: 25 });
  });

  it('uses the thread, read, reply, classification and unread-count routes', () => {
    service.thread('t1').subscribe();
    http.expectOne({ method: 'GET', url: '/api/v1/inbox/threads/t1' }).flush({});

    service.setRead('t1', false).subscribe();
    const read = http.expectOne({ method: 'POST', url: '/api/v1/inbox/threads/t1/read' });
    expect(read.request.body).toEqual({ isRead: false });
    read.flush(null);

    service.reply('t1', { clientRequestId: 'r1', body: 'hi' }).subscribe();
    const reply = http.expectOne({ method: 'POST', url: '/api/v1/inbox/threads/t1/reply' });
    expect(reply.request.body).toEqual({ clientRequestId: 'r1', body: 'hi' });
    reply.flush({});

    service.reclassify('t1', 'Unsubscribe').subscribe();
    const cls = http.expectOne({ method: 'POST', url: '/api/v1/inbox/threads/t1/classification' });
    expect(cls.request.body).toEqual({ classification: 'Unsubscribe' });
    cls.flush({});

    service.unreadCount().subscribe();
    http.expectOne({ method: 'GET', url: '/api/v1/inbox/unread-count' }).flush({ count: 3 });
  });
});
