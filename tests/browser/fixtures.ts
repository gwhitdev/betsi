import { test as base, expect, Page, APIRequestContext } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { randomUUID } from 'node:crypto';
import { writeFile } from 'node:fs/promises';

export const tenantA = '11111111-1111-1111-1111-111111111111';
export const tenantB = '22222222-2222-2222-2222-222222222222';
type Episode = { id: string; name: string; tenant: string };

export class Fixtures {
  private episodes: Episode[] = [];
  private escalations: { id: string; tenant: string }[] = [];
  constructor(private request: APIRequestContext) {}
  async post(path: string, data: object, tenant = tenantA, role = 'Nurse in Charge') {
    const response = await this.request.post(path, { data, headers: {
      'X-Betsi-Tenant': tenant, 'X-Betsi-Actor-Role': role
    } });
    expect(response.ok(), await response.text()).toBeTruthy();
    return response.json();
  }
  async get(path: string, tenant = tenantA) {
    const response = await this.request.get(path, { headers: {
      'X-Betsi-Tenant': tenant, 'X-Betsi-Actor-Role': 'Nurse in Charge'
    } });
    expect(response.ok(), await response.text()).toBeTruthy();
    return response.json();
  }
  async patient(tenant = tenantA) {
    const firstName = `Browser${randomUUID().replaceAll('-', '').slice(0, 10)}`;
    const result = await this.post('/api/v1/patients/register', {
      firstName, lastName: 'Synthetic', dateOfBirth: '1980-01-01'
    }, tenant);
    const episode = { id: result.aggregateId, name: `${firstName} Synthetic`, tenant };
    this.episodes.push(episode);
    return episode;
  }
  async child(tenant = tenantA) {
    const firstName = `BrowserChild${randomUUID().replaceAll('-', '').slice(0, 8)}`;
    const result = await this.post('/api/v1/patients/register', {
      firstName, lastName: 'Synthetic', dateOfBirth: '2018-01-01'
    }, tenant);
    const episode = { id: result.aggregateId, name: `${firstName} Synthetic`, tenant };
    this.episodes.push(episode);
    return episode;
  }
  rememberEpisode(id: string, name: string, tenant = tenantA) {
    this.episodes.push({ id, name, tenant });
  }
  async location(tenant = tenantA) {
    const name = `Treatment ${randomUUID().replaceAll('-', '').slice(0, 8)}`;
    const result = await this.post('/api/v1/locations', { name, capacity: 2 }, tenant, 'Site Administrator');
    return { id: result.aggregateId, name };
  }
  async escalate(episode: Episode) {
    const result = await this.post('/api/v1/escalations/waiting-time', {
      patientEpisodeId: episode.id, locationId: randomUUID(), responsibleRole: 'Nurse in Charge'
    }, episode.tenant);
    this.escalations.push({ id: result.aggregateId, tenant: episode.tenant });
    return result.aggregateId;
  }
  async cleanup() {
    for (const escalation of this.escalations) {
      const board = await this.get('/api/v1/escalations/board', escalation.tenant);
      if (board.awaitingAcknowledgement.some((item: { id: string }) => item.id === escalation.id))
        await this.post(`/api/v1/escalations/${escalation.id}/acknowledge`, {}, escalation.tenant);
      await this.post(`/api/v1/escalations/${escalation.id}/resolve`, {}, escalation.tenant);
    }
    for (const episode of this.episodes) {
      const board = await this.get('/api/v1/escalations/board', episode.tenant);
      const awaiting = board.awaitingAcknowledgement.filter(
        (item: { patient?: { episodeId: string } }) => item.patient?.episodeId === episode.id);
      const active = board.active.filter(
        (item: { patient?: { episodeId: string } }) => item.patient?.episodeId === episode.id);
      for (const item of awaiting) {
        await this.post(`/api/v1/escalations/${item.id}/acknowledge`, {}, episode.tenant);
        await this.post(`/api/v1/escalations/${item.id}/resolve`, {}, episode.tenant);
      }
      for (const item of active)
        await this.post(`/api/v1/escalations/${item.id}/resolve`, {}, episode.tenant);

      const detail = await this.get(`/api/v1/episodes/${episode.id}`, episode.tenant);
      if (detail.state !== 'Discharged' && detail.state !== 'Cancelled') {
        await this.post(`/api/v1/patients/${episode.id}/cancel`, {
          expectedVersion: detail.version, reason: 'Browser fixture cleanup'
        }, episode.tenant);
      }
    }
  }
}

export const test = base.extend<{ fixtures: Fixtures }>({
  fixtures: async ({ request }, use) => {
    const fixtures = new Fixtures(request);
    try { await use(fixtures); } finally { await fixtures.cleanup(); }
  }
});
export { expect };

export async function signIn(page: Page, username = 'nurse', path = '/waiting') {
  await page.goto(path);
  await page.getByRole('link', { name: 'Sign in', exact: true }).click();
  await page.locator('#username').fill(username);
  await page.locator('#password').fill('betsi');
  await page.locator('#kc-login').click();
  if (page.url().includes('/login-actions/authenticate') &&
      await page.getByText('Your login attempt timed out', { exact: false }).isVisible().catch(() => false)) {
    await page.locator('#username').fill(username);
    await page.locator('#password').fill('betsi');
    await page.locator('#kc-login').click();
  }
  await expect(page).toHaveURL(new RegExp(`${path}$`));
}

export async function axe(page: Page, label: string) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
  const report = test.info().outputPath(`axe-${label}.json`);
  await writeFile(report, JSON.stringify(results, null, 2));
  await test.info().attach(`axe-${label}`, {
    path: report, contentType: 'application/json'
  });
  const screenshot = test.info().outputPath(`screen-${label}.png`);
  await page.screenshot({ path: screenshot, fullPage: true });
  await test.info().attach(`screen-${label}`, {
    path: screenshot, contentType: 'image/png'
  });
  expect(results.violations).toEqual([]);
}
