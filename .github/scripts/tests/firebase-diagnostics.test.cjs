const assert = require('node:assert/strict');
const test = require('node:test');
const { check, report } = require('../firebase-diagnostics.cjs');

const appId = '1:123456:android:abcdef';
const credentials = { type: 'service_account', client_email: 'ci@identity-fixture.iam.gserviceaccount.com',
  project_id: 'identity-fixture', private_key: 'PRIVATE-KEY-MUST-NOT-LEAK' };
const error = { error: { code: 403, status: 'PERMISSION_DENIED', message: 'RAW-ERROR-MUST-NOT-LEAK',
  details: [{ '@type': 'type.googleapis.com/google.rpc.ErrorInfo', reason: 'SERVICE_DISABLED',
    domain: 'googleapis.com', metadata: { service: 'firebaseappdistribution.googleapis.com',
      consumer: 'projects/123456', private_key: credentials.private_key, access_token: 'TOKEN-MUST-NOT-LEAK' } }] } };

async function run(responses) {
  const records = [], requests = [];
  await check({ credentials, appId, packageName: 'com.arriopto.fixture', write: record => records.push(record),
    request: async options => {
      requests.push(options);
      const result = responses[requests.length - 1];
      if (result instanceof Error) throw result;
      return result;
    } });
  return { records, requests };
}

test('resolves the actual target, reads app metadata and tests both release permissions without uploading', async () => {
  const { records, requests } = await run([
    { status: 200, data: { projectId: 'target-fixture', projectNumber: '123456' } },
    { status: 200, data: { packageName: 'com.arriopto.fixture' } },
    { status: 200, data: { permissions: ['firebaseappdistro.releases.list', 'firebaseappdistro.releases.update'] } },
    { status: 200, data: { releases: [{ binaryDownloadUri: 'SIGNED-URL-MUST-NOT-LEAK' }] } },
  ]);
  assert.equal(requests.length, 4);
  assert.equal(requests[2].url, 'https://cloudresourcemanager.googleapis.com/v1/projects/target-fixture:testIamPermissions');
  assert.deepEqual(requests.filter(request => request.method === 'POST').map(request => new URL(request.url).pathname),
    ['/v1/projects/target-fixture:testIamPermissions']);
  assert.equal(records.find(record => record.check === 'android-package').matches, true);
  assert.deepEqual(records.find(record => record.check === 'release-permissions').missing, []);
  assert.ok(records.some(record => record.credentialProjectId === 'identity-fixture' && record.targetProjectNumber === '123456'));
  assert.ok(!JSON.stringify(records).includes('MUST-NOT-LEAK'));
});

test('reports missing write permission even when reading releases succeeds', async () => {
  const { records } = await run([
    { status: 200, data: { projectId: 'target-fixture' } },
    { status: 200, data: { packageName: 'com.other.app' } },
    { status: 200, data: { permissions: ['firebaseappdistro.releases.list'] } },
    { status: 200, data: {} },
  ]);
  assert.deepEqual(records.find(record => record.check === 'release-permissions').missing,
    ['firebaseappdistro.releases.update']);
  assert.equal(records.find(record => record.check === 'android-package').matches, false);
});

test('auxiliary API failures remain diagnostic and include safe service-disabled information', async () => {
  const failure = Object.assign(new Error('AUTH-ERROR-MUST-NOT-LEAK'), {
    response: { status: 403, data: error }, config: { headers: { Authorization: 'Bearer TOKEN-MUST-NOT-LEAK' } },
  });
  const { records, requests } = await run([failure, failure, failure, { status: 403, data: error }]);
  assert.equal(requests.length, 4);
  assert.ok(requests[2].url.includes('/projects/123456:testIamPermissions'));
  assert.equal(records.find(record => record.check === 'app-distribution-read').details[0].reason, 'SERVICE_DISABLED');
  assert.ok(!records.some(record => record.check === 'release-permissions'));
  assert.ok(!JSON.stringify(records).includes('MUST-NOT-LEAK'));
});

test('an empty permissions result means no requested permissions were returned', async () => {
  const { records } = await run([{ status: 403, data: error }, { status: 404, data: {} },
    { status: 200, data: {} }, { status: 404, data: {} }]);
  assert.equal(records.find(record => record.check === 'release-permissions').missing.length, 2);
});

test('filters actual CLI response log lines without echoing credentials, tokens, signed links or raw messages', () => {
  const records = [];
  const url = `https://firebaseappdistribution.googleapis.com/upload/v1/projects/123456/apps/${appId}/releases:upload`;
  const log = [
    'Authorization: Bearer TOKEN-MUST-NOT-LEAK',
    `>>> [apiv2][body] POST ${url} ${JSON.stringify(error)}`,
    `<<< [apiv2][body] POST https://oauth2.googleapis.com/token ${JSON.stringify(error)}`,
    `[debug] [2026-10-31T00:00:00Z] <<< [apiv2][body] POST ${url} ${JSON.stringify(error)}`,
    `<<< [apiv2][body] POST ${url} {invalid-json}`,
    `<<< [apiv2][body] GET ${url} {"binaryDownloadUri":"SIGNED-URL-MUST-NOT-LEAK"}`,
  ].join('\n');
  report(log, record => records.push(record));
  assert.equal(records.length, 1);
  assert.equal(records[0].httpStatus, 403);
  assert.equal(records[0].operation, 'upload');
  assert.equal(records[0].details[0].metadata.consumer, 'projects/123456');
  assert.ok(!JSON.stringify(records).includes('MUST-NOT-LEAK'));
});

test('absent logs and invalid ADC are handled without exposing raw errors', async () => {
  const records = [];
  report('', record => records.push(record));
  assert.match(records[0].note, /No structured/);
  let requested = false;
  await assert.rejects(check({ credentials: { ...credentials, type: 'authorized_user' }, appId,
    request: () => { requested = true; }, write: () => {} }), /service-account ADC/);
  assert.equal(requested, false);
});
