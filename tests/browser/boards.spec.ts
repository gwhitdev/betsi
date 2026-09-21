import { test, expect, signIn, tenantB } from './fixtures';

test('signed-out visitor can log in and navigate both boards', async ({ page }) => {
  await signIn(page);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Waiting room board');
  await expect(page.locator('.live-status')).toHaveClass(/live-status--on/);
  await page.locator('nav a[href="/escalations"]').click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Escalation dashboard');
  await expect(page.locator('.live-status')).toHaveClass(/live-status--on/);
});

test('both boards isolate departments, including live updates', async ({ page, browser, fixtures }) => {
  const local = await fixtures.patient();
  const foreign = await fixtures.patient(tenantB);
  await fixtures.escalate(local);
  await fixtures.escalate(foreign);
  await signIn(page);
  const otherContext = await browser.newContext({ baseURL: 'http://localhost:8080' });
  try {
    const other = await otherContext.newPage();
    await signIn(other, 'wrexham');
    for (const path of ['/waiting', '/escalations']) {
      await page.goto(path);
      await other.goto(`http://localhost:8080${path}`);
      await expect(page.locator('main')).toContainText(local.name);
      await expect(page.locator('main')).not.toContainText(foreign.name);
      await expect(other.locator('main')).toContainText(foreign.name);
      await expect(other.locator('main')).not.toContainText(local.name);
      await expect(page.locator('.live-status')).toHaveClass(/--on/);
    }
    const later = await fixtures.patient(tenantB);
    await fixtures.escalate(later);
    await expect(other.locator('main')).toContainText(later.name);
    await expect(page.locator('main')).not.toContainText(later.name);
  } finally { await otherContext.close(); }
});

test('acknowledge and resolve persist and leave an audit trail', async ({ page, fixtures }) => {
  const patient = await fixtures.patient();
  const id = await fixtures.escalate(patient);
  await signIn(page, 'charge', '/escalations');
  await expect(page.getByRole('button', { name: `Resolve — ${patient.name}`, exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: `Acknowledge — ${patient.name}`, exact: true }).click();
  await expect(page.locator('section[aria-labelledby="active-heading"]')).toContainText(patient.name);
  await page.getByRole('button', { name: `Resolve — ${patient.name}`, exact: true }).click();
  await expect(page.locator('section[aria-labelledby="awaiting-heading"]')).not.toContainText(patient.name);
  await expect(page.locator('section[aria-labelledby="active-heading"]')).not.toContainText(patient.name);
  await expect(page.locator('section[aria-labelledby="history-heading"]')).toContainText(patient.name);
  const audit = await fixtures.get(`/api/v1/escalations/${id}/audit`);
  const text = JSON.stringify(audit);
  expect(text).toContain('EscalationAcknowledged');
  expect(text).toContain('EscalationResolved');
  expect(text).toContain('Nurse in Charge');
});

test('an escalation can be reassigned and its full history inspected', async ({ page, fixtures }) => {
  const patient = await fixtures.patient();
  await fixtures.escalate(patient);
  await signIn(page, 'charge', '/escalations');

  const row = page.locator('section[aria-labelledby="awaiting-heading"] tr').filter({ hasText: patient.name });
  await row.locator('summary').filter({ hasText: 'Reassign' }).click();
  await row.getByLabel('Responsible role').fill('Bed Manager');
  await row.getByLabel('Reason for reassignment').fill('Bed coordination is now required');
  await row.getByRole('button', { name: 'Reassign', exact: true }).click();

  const reassigned = page.locator('section[aria-labelledby="awaiting-heading"] tr').filter({ hasText: patient.name });
  await expect(reassigned).toContainText('Bed Manager');
  await reassigned.getByRole('button', { name: /Acknowledge/ }).click();
  const active = page.locator('section[aria-labelledby="active-heading"] tr').filter({ hasText: patient.name });
  await active.getByRole('button', { name: 'View history' }).click();
  const audit = page.locator('.audit-trail');
  await expect(audit).toContainText('Escalation created');
  await expect(audit).toContainText('Escalation reassigned');
  await expect(audit).toContainText('Escalation acknowledged');
});

test('registrations appear live within one second of command submission', async ({ page, fixtures }) => {
  await signIn(page);
  await expect(page.locator('.live-status')).toHaveClass(/--on/);
  const samples: number[] = [];
  for (let i = 0; i < 5; i++) {
    const submitted = performance.now();
    const patient = await fixtures.patient();
    await expect(page.locator('main')).toContainText(patient.name, { timeout: 1000 });
    samples.push(performance.now() - submitted);
  }
  await test.info().attach('command-to-render-latency', {
    body: JSON.stringify({ unit: 'ms', samples, note: 'Command submission to observed browser text includes commit time: a conservative upper bound on commit-to-render latency.' }),
    contentType: 'application/json'
  });
  expect(Math.max(...samples)).toBeLessThan(1000);
});

test('escalation updates render within one second with 150 open escalations', async ({ page, fixtures }) => {
  test.setTimeout(180_000);
  for (let i = 0; i < 150; i++) await fixtures.escalate(await fixtures.patient());
  await signIn(page, 'charge', '/escalations');
  await expect(page.locator('.live-status')).toHaveClass(/--on/);
  await expect(page.locator('section[aria-labelledby="awaiting-heading"] tbody tr')).toHaveCount(150);
  const samples: number[] = [];
  for (let i = 0; i < 5; i++) {
    const patient = await fixtures.patient();
    const submitted = performance.now();
    await fixtures.escalate(patient);
    await expect(page.locator('main')).toContainText(patient.name, { timeout: 1000 });
    samples.push(performance.now() - submitted);
  }
  await test.info().attach('loaded-board-latency', {
    body: JSON.stringify({ unit: 'ms', initialOpenEscalations: 150, clients: 1, samples }),
    contentType: 'application/json'
  });
  expect(Math.max(...samples)).toBeLessThan(1000);
});

test('site administrator cannot view either patient board', async ({ page, fixtures }) => {
  const patient = await fixtures.patient();
  await fixtures.escalate(patient);
  await signIn(page, 'admin');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Your role does not permit that action.');
  await expect(page.locator('table')).toHaveCount(0);
  await expect(page.locator('main')).not.toContainText(patient.name);
  await page.locator('nav a[href="/escalations"]').click();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Your role does not permit that action.');
  await expect(page.locator('table')).toHaveCount(0);
  await expect(page.locator('main')).not.toContainText(patient.name);
});
