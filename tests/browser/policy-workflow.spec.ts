import { test, expect, signIn, axe } from './fixtures';

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
