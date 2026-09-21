import { test, expect, signIn, axe } from './fixtures';

test('policy preview is invalidated by every draft input and tier edit', async ({ page }) => {
  await signIn(page, 'admin', '/policy');
  await page.getByLabel('Recommended action').first().fill('Review the patient');
  const preview = page.getByRole('region', { name: 'Policy impact preview' });
  const refresh = async () => {
    await page.getByRole('button', { name: 'Preview impact' }).click();
    await expect(preview).toBeVisible();
    await expect(preview).toContainText(/Draft version \d+, previewed/);
  };
  const stale = async () => {
    await expect(preview).toBeHidden();
    await expect(page.getByRole('status')).toContainText('draft changed after the preview');
  };
  await refresh();
  await page.getByLabel('Threshold (minutes)').first().fill('241'); await stale(); await refresh();
  await page.getByLabel('Responsible role').first().fill('Senior nurse'); await stale(); await refresh();
  await page.getByLabel('Acknowledgement deadline (minutes)').first().fill('31'); await stale(); await refresh();
  await page.getByLabel('Recommended action').first().fill('Review promptly'); await stale(); await refresh();
  await page.getByLabel('Missed-deadline follow-up owner').fill('Senior Clinician'); await stale(); await refresh();
  await page.getByLabel('Reason for change').fill('Synthetic edit'); await stale(); await refresh();
  await page.getByRole('button', { name: 'Add tier' }).click(); await stale();
  await page.getByLabel('Threshold (minutes)').last().fill('480');
  await page.getByLabel('Recommended action').last().fill('Escalate review');
  await refresh();
  await page.getByRole('button', { name: 'Remove tier' }).last().click(); await stale(); await refresh();
  await page.getByLabel('Enable automatic waiting-time escalation').uncheck(); await stale();
});

test('policy proposal is previewed and independently approved', async ({ page, browser, fixtures }) => {
  const waiting = await fixtures.patient();
  const proposalReason = `Browser acceptance policy proposal ${Date.now()}`;
  await signIn(page, 'admin', '/policy');

  await page.getByLabel('Recommended action').fill('Review the patient and record the outcome.');
  await page.getByLabel('Reason for change').fill(proposalReason);
  await page.getByRole('button', { name: 'Preview impact' }).click();
  await expect(page.getByRole('region', { name: 'Policy impact preview' })).toContainText('1 patients are currently waiting.');
  await page.getByRole('button', { name: 'Submit proposal' }).click();
  await expect(page.getByRole('status')).toContainText('requires independent approval');
  await expect(page.getByRole('article').filter({ hasText: proposalReason })).toContainText('Proposed');
  await axe(page, 'policy-proposed');

  const approverContext = await browser.newContext({ baseURL: 'http://localhost:8080' });
  try {
    const approver = await approverContext.newPage();
    await signIn(approver, 'lead', '/policy');
    const proposal = approver.getByRole('article').filter({ hasText: proposalReason });
    await proposal.getByLabel('Decision or withdrawal reason').fill('Independent clinical approval');
    await proposal.getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(approver.getByRole('status')).toHaveText('The policy revision was approved.');
    await expect(approver.getByText('Currently in force', { exact: true }).first()).toBeVisible();
  } finally {
    await approverContext.close();
  }

  await page.reload();
  await expect(page.getByText('Currently in force', { exact: true }).first()).toBeVisible();
  await expect(page.locator('main')).toContainText('Independent clinical approval');
  await expect(page.locator('main')).not.toContainText(waiting.name);
});
