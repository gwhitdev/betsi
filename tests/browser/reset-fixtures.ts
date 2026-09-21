import { request } from '@playwright/test';
import { Fixtures, tenantA, tenantB } from './fixtures';

// The host uses only betsi_browser_* databases. Recover synthetic fixtures left by an
// interrupted prior run through audited commands; never erase databases or real episodes.
export default async function reset() {
  const api = await request.newContext({ baseURL: 'http://localhost:8080' });
  try {
    const fixtures = new Fixtures(api);
    for (const tenant of [tenantA, tenantB]) {
      const board = await fixtures.get('/api/v1/escalations/board', tenant);
      for (const card of board.active) {
        if (!/^Browser/.test(card.patient?.name ?? '')) continue;
        if (board.awaitingAcknowledgement.some((entry: { id: string }) => entry.id === card.id))
          await fixtures.post(`/api/v1/escalations/${card.id}/acknowledge`, {}, tenant);
        await fixtures.post(`/api/v1/escalations/${card.id}/resolve`, {}, tenant);
      }
      let cursor: string | null = null;
      do {
        const waiting = await fixtures.get(`/api/v1/boards/waiting?pageSize=200${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`, tenant);
        for (const row of waiting.items) {
          if (!/^Browser/.test(row.name)) continue;
          await fixtures.post(`/api/v1/patients/${row.episodeId}/cancel`, {
            expectedVersion: row.version, reason: 'Interrupted browser fixture cleanup'
          }, tenant);
        }
        cursor = waiting.nextCursor;
      } while (cursor);
    }
  } finally { await api.dispose(); }
}
