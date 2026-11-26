import { ChangeDetectorRef } from '@angular/core';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { Unsubscribe } from './unsubscribe';
import { UnsubscribeInfo, UnsubscribeService } from './unsubscribe.service';

describe('Unsubscribe', () => {
  const ready: UnsubscribeInfo = { email: 'j***@acme.com', alreadyUnsubscribed: false };

  const build = (token: string | null, info = vi.fn().mockReturnValue(of(ready)), confirm = vi.fn().mockReturnValue(of(undefined))) => {
    const route = { snapshot: { paramMap: convertToParamMap(token ? { token } : {}) } } as unknown as ActivatedRoute;
    const service = { info, confirm } as unknown as UnsubscribeService;
    const component = new Unsubscribe(route, service, { markForCheck: vi.fn() } as unknown as ChangeDetectorRef);
    return { component, info, confirm };
  };

  it('loads the masked address on open and never posts by itself', () => {
    const { component, info, confirm } = build('tok');

    component.ngOnInit();

    expect(info).toHaveBeenCalledWith('tok');
    expect(confirm).not.toHaveBeenCalled();
    expect(component.state).toBe('ready');
    expect(component.info?.email).toBe('j***@acme.com');
  });

  it('posts when the button is pressed and shows the success state', () => {
    const { component, confirm } = build('tok');
    component.ngOnInit();

    component.confirm();

    expect(confirm).toHaveBeenCalledWith('tok');
    expect(component.state).toBe('done');
  });

  it('ignores a second press while the first is in flight', () => {
    const { component, confirm } = build('tok');
    component.ngOnInit();
    component.submitting = true;

    component.confirm();

    expect(confirm).not.toHaveBeenCalled();
  });

  it('shows the already-unsubscribed state', () => {
    const { component } = build('tok', vi.fn().mockReturnValue(of({ ...ready, alreadyUnsubscribed: true })));
    component.ngOnInit();
    expect(component.state).toBe('already');
  });

  it('shows the invalid-link state for a 404', () => {
    const { component } = build('bad', vi.fn().mockReturnValue(throwError(() => ({ status: 404 }))));
    component.ngOnInit();
    expect(component.state).toBe('invalid');
  });

  it('is invalid without a token and calls nothing', () => {
    const { component, info } = build(null);
    component.ngOnInit();
    expect(component.state).toBe('invalid');
    expect(info).not.toHaveBeenCalled();
  });

  it('shows a generic error when the post fails for another reason', () => {
    const { component } = build('tok', undefined, vi.fn().mockReturnValue(throwError(() => ({ status: 500 }))));
    component.ngOnInit();
    component.confirm();
    expect(component.state).toBe('error');
    expect(component.submitting).toBe(false);
  });
});
