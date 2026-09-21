import { test, expect, signIn, tenantB, axe } from './fixtures';
import { execFileSync } from 'node:child_process';

function setBrowserTenantDatabase(online: boolean) {
  const state = online ? 'ONLINE' : 'OFFLINE WITH ROLLBACK IMMEDIATE';
  execFileSync('docker', ['exec', 'betsi-sqlserver', '/bin/bash', '-lc',
    `/opt/mssql-tools18/bin/sqlcmd -S localhost -d master -U sa -P "$MSSQL_SA_PASSWORD" -C -b -Q "ALTER DATABASE [betsi_browser_glan_clwyd] SET ${state}"`],
  { timeout: 20_000 });
}

test('failed initial episode load is announced and retry recovers', async ({ page, fixtures }) => {
  test.setTimeout(150_000);
  const patient = await fixtures.patient();
  await signIn(page, 'nurse', '/waiting');
  setBrowserTenantDatabase(false);
  try {
    await page.goto(`/episodes/${patient.id}`);
    await expect(page.getByRole('heading', { name: 'Patient episode could not be loaded' })).toBeVisible();
    await expect(page.getByRole('alert')).toContainText('Try again');
    await expect(page.getByRole('button', { name: 'Retry' })).toBeVisible();
    await expect(page.locator('main')).not.toContainText(patient.name);
  } finally {
    setBrowserTenantDatabase(true);
  }
  await expect.poll(async () => {
    try { return (await fixtures.get(`/api/v1/episodes/${patient.id}`)).state; }
    catch { return 'Recovering'; }
  }, { timeout: 45_000 }).toBe('Waiting');
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByRole('heading', { name: patient.name })).toBeVisible();
});

test('register a patient, record an observation and preserve a correction', async ({ page, fixtures }) => {
  await signIn(page, 'nurse', '/patients/register');

  await page.getByLabel('First name').fill('Browser');
  await page.getByLabel('Last name').fill('Workflow');
  await page.getByLabel('Date of birth').fill('1984-03-12');
  await page.getByRole('button', { name: 'Register patient' }).click();

  await expect(page).toHaveURL(/\/episodes\/[0-9a-f-]+$/);
  const episodeId = page.url().split('/').pop()!;
  fixtures.rememberEpisode(episodeId, 'Browser Workflow');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Browser Workflow');
  await expect(page.getByText('12/03/1984', { exact: true })).toBeVisible();

  await page.getByLabel('Respiratory rate').fill('16');
  await page.getByLabel('Oxygen saturation').fill('98');
  await page.getByLabel('Pain score').fill('6');
  await page.getByLabel('Pain scale').selectOption('NumericRating');
  await page.getByLabel('Pain location').fill('Left shoulder');
  await page.getByLabel('Pain character').fill('Aching');
  await page.getByLabel('Pain onset').fill(new Date(Date.now() - 30 * 60_000).toISOString().slice(0, 16));
  await page.getByLabel('Observation source').selectOption('NursingAssessment');
  await page.getByLabel('Situation').fill('New shoulder pain while waiting');
  await page.getByLabel('Background').fill('No reported injury');
  await page.getByLabel('Assessment', { exact: true }).fill('Pain requires review');
  await page.getByLabel('Recommendation').fill('Review analgesia');
  await page.getByLabel('Breathing finding', { exact: true }).selectOption('NoConcern');
  await page.getByLabel('Breathing finding details').fill('Speaking comfortably');
  await page.getByLabel('Mobility finding', { exact: true }).selectOption('Concern');
  await page.getByLabel('Mobility finding details').fill('Guarding left arm');
  await page.getByLabel(/I have confirmed this is Browser Workflow/).check();
  await page.getByRole('button', { name: 'Save observation' }).click();
  await expect(page.getByRole('status')).toHaveText('The observation was saved.');
  await expect(page.locator('.observation')).toHaveCount(1);
  await expect(page.locator('.observation')).toContainText('16');
  await expect(page.locator('.observation')).toContainText('Left shoulder');
  await expect(page.locator('.observation')).toContainText('New shoulder pain while waiting');
  await expect(page.locator('.observation')).toContainText('Speaking comfortably');

  await page.getByRole('button', { name: 'Correct', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Correct an observation' })).toBeVisible();
  await expect(page.getByLabel('Respiratory rate')).toHaveValue('16');
  await page.getByLabel('Respiratory rate').fill('18');
  await page.getByLabel(/I have confirmed this is Browser Workflow/).check();
  await page.getByRole('button', { name: 'Save observation' }).click();
  await expect(page.locator('.observation')).toHaveCount(2);
  await expect(page.locator('.observation').filter({ hasText: 'Superseded' })).toContainText('16');
  await expect(page.locator('.observation').filter({ hasText: 'Correction' })).toContainText('18');
  await expect(page.locator('.observation').filter({ hasText: 'Correction' })).toContainText('Review analgesia');

  await axe(page, 'episode-observation-history');
});

test('episode detail does not disclose another department', async ({ page, fixtures }) => {
  const foreign = await fixtures.patient(tenantB);
  await signIn(page);
  await page.goto(`/episodes/${foreign.id}`);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Patient episode not found');
  await expect(page.locator('main')).not.toContainText(foreign.name);
});

test('paediatric clinician assignment records training status', async ({ page, fixtures }) => {
  const child = await fixtures.child();
  await signIn(page, 'nurse', `/episodes/${child.id}`);

  await expect(page.getByRole('group', { name: 'Paediatric staff assignment' })).toBeVisible();
  await page.getByLabel('I have recorded paediatric training for this work').check();
  await page.getByRole('button', { name: 'Assign myself' }).click();

  await expect(page.getByRole('status')).toHaveText('The clinical staff assignment was recorded.');
  await expect(page.getByText('Training recorded', { exact: true })).toBeVisible();
  await axe(page, 'episode-paediatric-assignment');
});

test('registration and episode workflow has complete Welsh UI', async ({ page, fixtures }) => {
  const patient = await fixtures.patient();
  await signIn(page, 'nurse', `/episodes/${patient.id}`);
  await page.locator('.language-picker select').selectOption('cy-GB');
  await expect(page.locator('html')).toHaveAttribute('lang', 'cy');
  await expect(page.getByRole('heading', { level: 2, name: 'Cofnodi arsylwadau' })).toBeVisible();
  await expect(page.getByText('Hanes arsylwadau')).toBeVisible();
  await axe(page, 'episode-welsh');
});

test('clinical safety and patient flow actions are completed from episode detail', async ({ page, fixtures }) => {
  const patient = await fixtures.patient();
  const location = await fixtures.location();
  await signIn(page, 'nurse', `/episodes/${patient.id}`);

  await page.getByLabel('A carer is present').check();
  await page.getByLabel('Carer name').fill('Synthetic Carer');
  await page.getByLabel('Relationship to patient').fill('Parent');
  await page.getByRole('button', { name: 'Save carer presence' }).click();
  await expect(page.getByRole('status')).toHaveText('Carer presence was recorded.');
  await expect(page.getByText('Synthetic Carer (Parent)')).toBeVisible();

  await page.getByLabel('What was observed').fill('Synthetic safeguarding concern');
  await page.getByRole('button', { name: 'Raise concern' }).click();
  await expect(page.getByRole('status')).toContainText('safeguarding concern was raised');

  await page.getByLabel('What deterioration was observed').fill('Synthetic deterioration');
  await page.getByRole('button', { name: 'Raise deterioration alert' }).click();
  await expect(page.getByRole('status')).toContainText('deterioration alert was raised');

  await page.getByRole('button', { name: 'Begin triage' }).click();
  await expect(page.getByRole('button', { name: 'Complete triage' })).toBeVisible();
  await page.getByRole('button', { name: 'Complete triage' }).click();
  await page.getByLabel('Treatment location').selectOption(location.id);
  await page.getByRole('button', { name: 'Begin treatment' }).click();
  await expect(page.getByText('In treatment', { exact: true })).toBeVisible();

  await page.getByLabel(/I confirm that .* is ready to be discharged/).check();
  await page.getByRole('button', { name: 'Discharge patient' }).click();
  await expect(page.getByRole('status')).toHaveText('The patient was discharged.');
  await expect(page.getByText('Discharged', { exact: true })).toBeVisible();
  await expect(page.getByText('This episode has ended.', { exact: false })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Raise deterioration alert' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Begin triage' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Save observation' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save carer presence' })).toBeVisible();
  await axe(page, 'episode-clinical-flow');
});

test('a concurrent episode change names the patient and explains recovery', async ({ page, fixtures }) => {
  const patient = await fixtures.patient();
  await signIn(page, 'nurse', `/episodes/${patient.id}`);
  const detail = await fixtures.get(`/api/v1/episodes/${patient.id}`);
  await fixtures.post(`/api/v1/patients/${patient.id}/triage/begin`, { expectedVersion: detail.version });
  await page.getByRole('button', { name: 'Begin triage' }).click();
  await expect(page.getByRole('alert')).toContainText(patient.name);
  await expect(page.getByRole('alert')).toContainText('Refresh it, review the latest details');
  await expect(page.getByRole('alert')).not.toContainText('AggregateConcurrencyException');
});

test('cancelled episodes show late recording but no prohibited deterioration action', async ({ page, fixtures }) => {
  const patient = await fixtures.patient();
  const detail = await fixtures.get(`/api/v1/episodes/${patient.id}`);
  await fixtures.post(`/api/v1/patients/${patient.id}/cancel`, {
    expectedVersion: detail.version, reason: 'Synthetic cancellation'
  });
  await signIn(page, 'nurse', `/episodes/${patient.id}`);
  await expect(page.getByText('This episode has ended.', { exact: false })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Raise deterioration alert' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Begin triage' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Save observation' })).toBeVisible();
});
