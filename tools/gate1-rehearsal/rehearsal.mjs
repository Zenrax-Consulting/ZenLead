// Gate 1 browser rehearsal: drives the real Angular UI against a running API + `ng serve`.
// Usage: node rehearsal.mjs <baseUrl> <outDir>      (see README.md in this folder)
import { chromium } from 'playwright-core';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const baseUrl = process.argv[2] ?? 'http://localhost:4300';
const outDir = process.argv[3] ?? './out';
mkdirSync(outDir, { recursive: true });

const email = `rehearsal-${Date.now()}@example.test`;
const password = 'Passw0rd!';
const log = [];
const step = (name, ok, detail = '') => {
  log.push({ name, ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ' — ' + detail : ''}`);
};

const browser = await chromium.launch({ channel: 'chrome', headless: true });
const page = await (await browser.newContext({ viewport: { width: 1280, height: 800 } })).newPage();
const shot = name => page.screenshot({ path: join(outDir, `${name}.png`), fullPage: true });
const refreshCalls = [];
page.on('request', r => { if (r.url().includes('/api/v1/auth/refresh')) refreshCalls.push(r.url()); });

try {
  // 1. Register in the UI and land authenticated
  await page.goto(`${baseUrl}/register`);
  await page.fill('input[formControlName=workspaceName]', 'Rehearsal Co');
  await page.fill('input[formControlName=displayName]', 'Rita Rehearsal');
  await page.fill('input[formControlName=email]', email);
  await page.fill('input[formControlName=password]', password);
  await shot('01-register-form');
  await page.click('button[type=submit]');
  await page.waitForURL('**/leads');
  step('Register lands on /leads (authenticated, no detour via /login)', true);

  // 2. Empty state + toolbar
  const empty = await page.locator('.empty-state').waitFor({ timeout: 10000 }).then(() => true, () => false);
  const toolbar = await page.locator('.app-toolbar').isVisible();
  await shot('02-leads-empty-state');
  step('Empty state shown for a new workspace', empty);
  step('Toolbar with logout visible when signed in', toolbar);

  // 3. Duplicate registration shows a specific message (fresh context so the first session is untouched)
  const dupPage = await (await browser.newContext()).newPage();
  await dupPage.goto(`${baseUrl}/register`);
  await dupPage.fill('input[formControlName=workspaceName]', 'Dup Co');
  await dupPage.fill('input[formControlName=displayName]', 'Dup');
  await dupPage.fill('input[formControlName=email]', email);
  await dupPage.fill('input[formControlName=password]', password);
  await dupPage.click('button[type=submit]');
  await dupPage.waitForSelector('.auth-error');
  const dupText = await dupPage.locator('.auth-error').innerText();
  await dupPage.screenshot({ path: join(outDir, '03-duplicate-registration.png') });
  step('Duplicate email shows a specific message', /already registered/i.test(dupText), dupText);
  await dupPage.context().close();

  // 4. Log out, then log back in
  await page.click('text=Log out');
  await page.waitForURL('**/login');
  const toolbarGone = !(await page.locator('.app-toolbar').isVisible());
  step('Logout returns to /login and hides the toolbar', toolbarGone);
  await page.fill('input[formControlName=email]', email);
  await page.fill('input[formControlName=password]', password);
  await shot('04-login-form');
  await page.click('button[type=submit]');
  await page.waitForURL('**/leads');
  step('Login with the same credentials lands on /leads', true);

  // 5. Add a lead
  await page.fill('input[formControlName=name]', 'Jane Doe');
  await page.fill('input[formControlName=email]', 'jane.doe@example.com');
  await page.fill('input[formControlName=title]', 'CTO');
  await page.click('button:has-text("Add lead")');
  await page.waitForSelector('td:has-text("Jane Doe")');
  await shot('05-lead-added');
  step('Lead is created and listed', true);

  // 6. Reload the tab: silent refresh must keep the session (no bounce to /login)
  refreshCalls.length = 0;
  await page.reload();
  await page.waitForSelector('td:has-text("Jane Doe")', { timeout: 15000 });
  const stillOnLeads = page.url().endsWith('/leads');
  await shot('06-after-reload');
  step('Browser refresh keeps the session via silent /auth/refresh', stillOnLeads && refreshCalls.length >= 1, `refresh calls: ${refreshCalls.length}, url: ${page.url()}`);

  // 7. Generate a draft and time it
  await page.click('a:has-text("Jane Doe")');
  await page.waitForURL('**/leads/*');
  await shot('07-lead-detail');
  const started = Date.now();
  await page.click('button:has-text("Generate draft")');
  await page.waitForSelector('mat-card textarea', { timeout: 30000 });
  const elapsedMs = Date.now() - started;
  const subject = await page.locator('mat-card textarea').nth(0).inputValue();
  const body = await page.locator('mat-card textarea').nth(1).inputValue();
  await shot('08-draft-generated');
  step('Draft generated and shown in editable fields', subject.length > 0 && body.length > 0, `${elapsedMs} ms (click to rendered), subject: "${subject}"`);
  step('Draft is personalised (mentions the lead)', /jane/i.test(body) || /jane/i.test(subject));
  step('Draft rendered in under ~5 s', elapsedMs < 5000, `${elapsedMs} ms`);

  // 8. Back link and expired-session behaviour
  await page.click('text=Back to leads');
  await page.waitForURL('**/leads');
  step('Back link returns to the leads list', true);

  // Corrupt the in-memory access token path by clearing the stored refresh token and reloading: must go to /login
  await page.evaluate(() => localStorage.removeItem('zenlead_refresh_token'));
  await page.reload();
  await page.waitForURL('**/login', { timeout: 15000 });
  await shot('09-signed-out-redirect');
  step('Without a refresh token a protected page redirects to /login', true);
} catch (err) {
  step('Unexpected error', false, String(err));
  await shot('99-error').catch(() => {});
} finally {
  await browser.close();
  writeFileSync(join(outDir, 'results.json'), JSON.stringify({ email, baseUrl, log }, null, 2));
}

process.exit(log.every(s => s.ok) ? 0 : 1);
