/** Test helper: a document stand-in whose visibility the test controls (works with rxjs fromEvent). */
export class FakeDocument extends EventTarget {
  visibilityState: DocumentVisibilityState = 'visible';
  setVisible(visible: boolean): void {
    this.visibilityState = visible ? 'visible' : 'hidden';
    this.dispatchEvent(new Event('visibilitychange'));
  }
}
