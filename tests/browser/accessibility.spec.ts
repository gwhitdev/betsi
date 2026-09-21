import { test, expect, signIn, axe } from './fixtures';

for (const path of ['/waiting', '/escalations']) {
  for (const culture of ['en-GB', 'cy-GB']) {
    for (const state of ['empty', 'populated', 'disconnected']) {
      test(`${path} ${culture} ${state} accessibility`, async ({ page, context, fixtures }) => {
        if (state !== 'empty') {
          const patient = await fixtures.patient();
          if (path === '/escalations') await fixtures.escalate(patient);
        }
        await signIn(page, 'nurse', path);
        if (culture !== 'en-GB') {
          await page.locator('#language').selectOption(culture);
          await expect(page.locator('html')).toHaveAttribute('lang', 'cy');
        }
        await expect(page.locator('.live-status')).toHaveClass(/--on/);
        if (state === 'empty') {
          if (path === '/waiting') await expect(page.locator('table')).toHaveCount(0);
          else {
            await expect(page.locator('section[aria-labelledby="awaiting-heading"] table')).toHaveCount(0);
            await expect(page.locator('section[aria-labelledby="active-heading"] table')).toHaveCount(0);
          }
        }
        else await expect(page.locator('table').first()).toBeVisible();
        if (state === 'disconnected') {
          await context.setOffline(true);
          await expect(page.locator('#reconnect-banner')).toBeVisible();
        }
        try { await axe(page, `${path.slice(1)}-${culture}-${state}`); }
        finally { await context.setOffline(false); }
      });
    }
  }
}

test('keyboard navigation, skip link and narrow viewport', async ({ page, fixtures }) => {
  const patient = await fixtures.patient();
  await signIn(page);
  await expect(page.locator('h1')).toBeFocused();
  // The router focuses the page heading. Walk backwards through the evolving header by keyboard.
  for (let i = 0; i < 12 && !await page.locator('.skip-link').evaluate(element => element === document.activeElement); i++)
    await page.keyboard.press('Shift+Tab');
  await expect(page.locator('.skip-link')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('main')).toBeFocused();
  await page.locator('nav a[href="/escalations"]').focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('h1')).toBeFocused();
  await page.goto('/waiting');
  await page.setViewportSize({ width: 400, height: 800 });
  await expect(page.locator('main')).toContainText(patient.name);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
  await axe(page, 'narrow-waiting');
});

for (const culture of ['en-GB', 'cy-GB']) {
  test(`failed escalation action remains visible and accessible in ${culture}`, async ({ page, fixtures }) => {
    // Drop only board notifications to model a concurrent change not yet seen by this user.
    let dropChanges = false;
    await page.routeWebSocket(/\/hub\/boards\?/, socket => {
      const server = socket.connectToServer();
      server.onMessage(message => {
        if (!dropChanges || !message.toString().includes('"target":"Changed"')) socket.send(message);
      });
    });
    const patient = await fixtures.patient();
    const id = await fixtures.escalate(patient);
    await signIn(page, 'charge', '/escalations');
    if (culture === 'cy-GB') {
      await page.locator('#language').selectOption(culture);
      await expect(page.locator('html')).toHaveAttribute('lang', 'cy');
    }
    await expect(page.locator('.live-status')).toHaveClass(/--on/);
    dropChanges = true;
    await fixtures.post(`/api/v1/escalations/${id}/acknowledge`, {});
    await fixtures.post(`/api/v1/escalations/${id}/resolve`, {});
    await page.locator('section[aria-labelledby="awaiting-heading"] tr').filter({ hasText: patient.name })
      .getByRole('button', { name: /Acknowledge|Cydnabod/ }).click();
    await expect(page.locator('.notice--error')).toBeVisible();
    await expect(page.locator('section[aria-labelledby="awaiting-heading"]')).not.toContainText(patient.name);
    await expect(page.locator('section[aria-labelledby="active-heading"]')).not.toContainText(patient.name);
    await expect(page.locator('section[aria-labelledby="history-heading"]')).toContainText(patient.name);
    await axe(page, `escalation-action-error-${culture}`);
  });
}

for (const path of ['/waiting', '/escalations']) {
  for (const culture of ['en-GB', 'cy-GB']) {
    test(`${path} ${culture} reflows at 400px and with 200 percent text`, async ({ page, fixtures }) => {
      const patient = await fixtures.patient();
      if (path === '/escalations') await fixtures.escalate(patient);
      await signIn(page, 'nurse', path);
      if (culture === 'cy-GB') {
        await page.locator('#language').selectOption(culture);
        await expect(page.locator('html')).toHaveAttribute('lang', 'cy');
      }
      await expect(page.locator('.live-status')).toHaveClass(/--on/);
      await page.setViewportSize({ width: 400, height: 800 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
      await expect(page.locator('.board-scroll').first()).toBeVisible();
      await axe(page, `${path.slice(1)}-${culture}-400px`);
      await page.setViewportSize({ width: 1280, height: 800 });
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
      await axe(page, `${path.slice(1)}-${culture}-200pct-text`);
    });
  }
}
