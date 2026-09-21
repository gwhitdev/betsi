import { test, expect, signIn } from './fixtures';

for (const path of ['/waiting', '/escalations']) {
  test(`${path} reconciles changes missed while offline`, async ({ page, context, fixtures }) => {
    await signIn(page, 'nurse', path);
    await expect(page.locator('.live-status')).toHaveClass(/--on/);
    await context.setOffline(true);
    await expect(page.locator('#reconnect-banner')).toBeVisible();
    await expect(page.locator('main')).toHaveAttribute('inert', '');
    const patient = await fixtures.patient();
    if (path === '/escalations') await fixtures.escalate(patient);
    await context.setOffline(false);
    await expect(page.locator('main')).toContainText(patient.name);
    await expect(page.locator('#reconnect-banner')).toBeHidden();
    await expect(page.locator('.live-status')).toHaveClass(/--on/);
  });
}

test('sign out removes access when a board is reopened', async ({ page }) => {
  await signIn(page);
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  // No ID token is stored in the Betsi cookie, so Keycloak asks for confirmation.
  await page.locator('#kc-logout').click();
  await expect(page).toHaveURL(/signed-out$/);
  await page.goto('/waiting');
  await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible();
  await expect(page.locator('table')).toHaveCount(0);
});

test('an expired login ends the live circuit and removes patient data', async ({ page, fixtures }) => {
  test.setTimeout(100_000);
  const patient = await fixtures.patient();
  await signIn(page);
  await expect(page.locator('main')).toContainText(patient.name);
  await expect(page.locator('.live-status')).toHaveClass(/--on/);
  // The isolated host issues one-minute cookies. No navigation or cookie manipulation:
  // the existing authenticated circuit must stop being usable when its ticket expires.
  await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible({ timeout: 80_000 });
  await expect(page.locator('main')).not.toContainText(patient.name);
  await expect(page.locator('table')).toHaveCount(0);
});

test('an initially unavailable board hub retries and reconciles missed changes', async ({ page, fixtures }) => {
  await page.route('**/hub/boards/negotiate*', route => route.fulfill({ status: 503 }));
  await signIn(page);
  await expect(page.locator('.live-status')).toHaveClass(/--off/);
  const patient = await fixtures.patient();
  await page.unroute('**/hub/boards/negotiate*');
  await expect(page.locator('.live-status')).toHaveClass(/--on/);
  await expect(page.locator('main')).toContainText(patient.name);
});

test('a frozen browser page recovers changes when resumed', async ({ page, context, fixtures }) => {
  await signIn(page);
  await expect(page.locator('.live-status')).toHaveClass(/--on/);
  await context.setOffline(true);
  await expect(page.locator('#reconnect-banner')).toBeVisible();
  const devtools = await context.newCDPSession(page);
  try {
    await devtools.send('Page.setWebLifecycleState', { state: 'frozen' });
    const patient = await fixtures.patient();
    await devtools.send('Page.setWebLifecycleState', { state: 'active' });
    await context.setOffline(false);
    await expect(page.locator('main')).toContainText(patient.name);
    await expect(page.locator('.live-status')).toHaveClass(/--on/);
    await expect(page.locator('#reconnect-banner')).toBeHidden();
  } finally {
    await devtools.send('Page.setWebLifecycleState', { state: 'active' });
    await context.setOffline(false);
    await devtools.detach();
  }
});
