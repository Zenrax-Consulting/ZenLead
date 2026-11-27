# T08 — Browser Testing (cross-browser, responsive, accessibility, visual)

**Branch:** `test/browser-matrix` · **Size:** M · **Depends on:** T07 skeleton (config, fixtures, page objects) · **Partly waits on:** F24, F27, F28 screens

## Goal
ZenLead works and looks right on the browsers internal users actually use, at desktop and narrow widths. It meets basic accessibility (F29.6 "accessibility basics"). Unintended visual changes are caught. Reuse the T07 suite instead of writing a second one.

## 1. Browser matrix (Playwright `projects` in `playwright.config.ts`)
| Project | Engine | Viewport | Runs | Why |
|---|---|---|---|---|
| `chromium` | Chromium | 1280×800 | Every PR: all `@regression` | Default |
| `chrome` | Branded Google Chrome (`channel: 'chrome'`) | 1440×900 | Nightly: `@smoke` + `@critical` | Main real-world browser |
| `msedge` | Branded Edge (`channel: 'msedge'`) | 1440×900 | Nightly: `@smoke` + `@critical` | Windows corporate default |
| `firefox` | Firefox | 1280×800 | Nightly: `@smoke` + `@critical` | Engine diversity |
| `webkit` | WebKit | 1280×800 | Nightly: `@smoke` + `@critical` | Safari proxy (owner or colleagues on macOS/iOS) |
| `mobile-chrome` | Chromium, `devices['Pixel 7']` | 412×915 | Nightly: `@responsive` | Shell hamburger, read-only use on phone |
| `mobile-safari` | WebKit, `devices['iPhone 14']` | 390×844 | Nightly: `@responsive` | Same |
| `tablet` | Chromium, 820×1180 | – | Release candidate: `@responsive` | Breakpoint between layouts |

Supported-browser statement to put in `docs/testing/supported-browsers.md`: last 2 versions of Chrome, Edge, Firefox and Safari. Mobile layouts are for reading and light actions (inbox reply, approve link), not the import wizard or the campaign builder.

## 2. Browser-specific risk checks (`tests/browser/compat.spec.ts`, run on every engine)
| Area | Check |
|---|---|
| Auth storage | Refresh token in `localStorage` survives reload on all engines. WebKit ITP doesn't break silent refresh. The access token is never persisted (any engine). |
| File upload | Import wizard drag-and-drop **and** the file input on Firefox and WebKit (DataTransfer differences). Large file progress. |
| Downloads | Errors CSV download (`page.waitForEvent('download')`) has the right filename and content on each engine. |
| Popups | "Job dashboard" `window.open` after the POST: not blocked when triggered by the click handler; documented fallback link otherwise (parking-lot F14). |
| Dates & time zones | Run with `timezoneId: 'America/New_York'` and `'Asia/Kolkata'` and `locale: 'en-GB'`. Send timeline, next-send times and analytics date ranges display in the workspace time zone consistently, and the DST week renders 7 days. |
| Clipboard / URL state | Shareable leads-list URL round trip. Reset token stripped from the address bar on every engine (`history.replaceState`). |
| Zoneless change detection | Async updates (poll results, job progress, dialogs) render without user interaction on every engine. This catches a missing `markForCheck()`. |
| SVG charts (F28) | In-house SVG trend/bar components render with correct dimensions on WebKit/Firefox. The table fallback is present. |
| Fonts/icons | Material icon font loads under the CSP (or the self-hosted font, F29/F30). No ligature text like "menu" showing instead of an icon. |

## 3. Responsive (`@responsive`)
- Shell: below 800 px the hamburger appears, the nav drawer opens/closes, the workspace name is still visible or in the menu (parking-lot F12).
- Leads list collapses to the documented columns, with no horizontal page scroll (`document.documentElement.scrollWidth <= innerWidth`).
- Inbox: list → thread → back button on narrow screens (F27 responsive back).
- Dialogs (create workspace, run discovery, enroll) fit the viewport and are scrollable.

## 4. Accessibility (`@a11y`, `@axe-core/playwright`)
- `tests/browser/a11y.spec.ts` visits every main screen (login, register, approve, forgot/reset, shell + leads list, lead detail, import wizard steps, target profiles, admin workspaces, campaigns list/builder, inbox list/thread, analytics, unsubscribe landing) and runs `new AxeBuilder({ page }).withTags(['wcag2a','wcag2aa'])`. **Fail on `serious`/`critical` violations.** Known exceptions go in `a11y-allowlist.ts` with a reason and an owner.
- Keyboard-only journey: login → leads → open lead → generate draft → back, using only Tab/Enter/Escape. Visible focus ring each step. Dialogs trap and restore focus.
- Accessible names: shell unread badge has an accessible label (F27), stat tiles expose numbers to screen readers, the chart has a table alternative (F28), form errors are linked via `aria-describedby`.
- Colour contrast is covered by axe. Also run the a11y spec with `colorScheme: 'dark'` if the theme supports it.

## 5. Visual regression (`@visual`, Chromium only, to avoid cross-engine pixel noise)
- `toHaveScreenshot()` on stable, seeded screens: login, empty leads state, leads list (seeded), lead detail, wizard map step, campaign builder with 3 steps, inbox thread, analytics dashboard (seeded fixed dates via the adjustable clock), unsubscribe page.
- Mask dynamic regions (timestamps, generated emails). `maxDiffPixelRatio: 0.01`. Baselines committed under `ZenLead.E2E/tests/browser/__screenshots__/` and generated on **one** agreed OS: Windows locally now, the Linux CI image once F30 exists, regenerated then.
- Visual tests are a review aid: they run on PRs touching `ZenLead.Client/**` and fail the PR only on unexpected diffs.

## 6. Run
```
npx playwright test --project=firefox --project=webkit --project=msedge --grep "@smoke|@critical"
npx playwright test --grep @a11y
npx playwright test --grep @visual --update-snapshots     # only when a change is intended
```
Nightly (scripted now, scheduled in CI by F30): `tools/test-runner/run.ps1 -Pack browser`.
