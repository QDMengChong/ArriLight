// Read-only Firebase access checks and filtered CLI error reporting. Never print raw credentials/errors.
const fs = require('node:fs');

const permissions = ['firebaseappdistro.releases.list', 'firebaseappdistro.releases.update'];

function publicValue(value, pattern) {
  return typeof value === 'string' && pattern.test(value) ? value : undefined;
}

function errorDetails(data) {
  const error = data?.error || {};
  const result = { status: publicValue(error.status, /^[A-Z_]{1,80}$/) };
  const metadataPatterns = {
    service: /^[a-z0-9.-]+\.googleapis\.com$/,
    consumer: /^projects\/[a-z0-9-]+$/,
    permission: /^[A-Za-z][A-Za-z0-9.]{1,150}$/,
    resource: /^projects\/[A-Za-z0-9._:/@-]{1,250}$/,
  };
  result.details = (Array.isArray(error.details) ? error.details : [])
    .filter(detail => detail?.['@type'] === 'type.googleapis.com/google.rpc.ErrorInfo')
    .map(detail => ({
      reason: publicValue(detail.reason, /^[A-Z][A-Z0-9_]{0,99}$/),
      domain: publicValue(detail.domain, /^[a-z0-9.-]{1,100}$/),
      metadata: Object.fromEntries(Object.entries(metadataPatterns)
        .map(([key, pattern]) => [key, publicValue(detail.metadata?.[key], pattern)])
        .filter(([, value]) => value !== undefined)),
    }));
  return result;
}

async function check({ credentials, appId, packageName, request, write }) {
  const match = /^1:([0-9]+):android:[0-9a-fA-F]+$/.exec(appId || '');
  const email = publicValue(credentials.client_email, /^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+\.gserviceaccount\.com$/);
  if (credentials.type !== 'service_account' || !email || !match) {
    throw new Error('Expected service-account ADC and a valid Firebase Android App ID.');
  }
  const projectNumber = match[1];
  write({ check: 'identity', serviceAccount: email,
    credentialProjectId: publicValue(credentials.project_id, /^[a-z][a-z0-9-]{4,61}[a-z0-9]$/),
    targetProjectNumber: projectNumber, appId });

  async function probe(label, options) {
    try {
      const response = await request({ ...options, timeout: 15000, retry: false });
      write({ check: label, httpStatus: response.status,
        ...(response.status >= 400 ? errorDetails(response.data) : {}) });
      return response.status >= 200 && response.status < 300 ? response.data : undefined;
    } catch (error) {
      // google-auth/gaxios errors may carry request headers, JWTs and private keys.
      write({ check: label, httpStatus: error.response?.status || 'unavailable',
        ...errorDetails(error.response?.data) });
      return undefined;
    }
  }

  const project = await probe('firebase-project', {
    url: `https://firebase.googleapis.com/v1beta1/projects/${projectNumber}`, method: 'GET',
  });
  const targetProjectId = publicValue(project?.projectId, /^[a-z][a-z0-9-]{4,61}[a-z0-9]$/);
  if (targetProjectId) write({ check: 'resolved-target-project', projectId: targetProjectId,
    projectNumber: publicValue(project.projectNumber, /^[0-9]+$/) });

  const app = await probe('firebase-android-app', {
    url: `https://firebase.googleapis.com/v1beta1/projects/${projectNumber}/androidApps/${encodeURIComponent(appId)}`,
    method: 'GET',
  });
  if (app) write({ check: 'android-package', expected: packageName,
    actual: publicValue(app.packageName, /^[A-Za-z][A-Za-z0-9_.]+$/),
    matches: app.packageName === packageName });

  const granted = await probe('project-permissions', {
    url: `https://cloudresourcemanager.googleapis.com/v1/projects/${targetProjectId || projectNumber}:testIamPermissions`,
    method: 'POST', data: { permissions },
  });
  if (granted && Array.isArray(granted.permissions)) {
    write({ check: 'release-permissions',
      granted: permissions.filter(permission => granted.permissions.includes(permission)),
      missing: permissions.filter(permission => !granted.permissions.includes(permission)) });
  } else if (granted) {
    write({ check: 'release-permissions', granted: [], missing: permissions });
  }

  await probe('app-distribution-read', {
    url: `https://firebaseappdistribution.googleapis.com/v1/projects/${projectNumber}/apps/${encodeURIComponent(appId)}/releases?pageSize=1`,
    method: 'GET',
  });
  write({ note: 'These are read-only diagnostics. A failed auxiliary API check is not proof of missing upload permission; a successful release read is not proof of write access. The CLI upload remains authoritative.' });
}

function report(log, write) {
  let found = false;
  for (const line of log.split(/\r?\n/)) {
    const match = /<<< \[apiv2\]\[body\] (GET|POST|PATCH|DELETE) (https:\/\/firebaseappdistribution\.googleapis\.com\/\S+) (\{.*\})$/.exec(line);
    if (!match) continue;
    try {
      const data = JSON.parse(match[3]);
      if (!Number.isInteger(data.error?.code) || data.error.code < 400) continue;
      found = true;
      write({ check: 'firebase-cli-error', method: match[1],
        operation: match[2].includes('/upload/v1/') ? 'upload'
          : match[2].includes(':distribute') ? 'distribute' : 'metadata',
        httpStatus: data.error.code, ...errorDetails(data) });
    } catch { /* Ignore non-JSON log lines, never echo their content. */ }
  }
  if (!found) write({ note: 'No structured App Distribution error found in the CLI log. No raw debug log or credentials are printed.' });
}

async function main() {
  const write = value => {
    const line = JSON.stringify(value);
    console.log(line);
    if (process.env.GITHUB_STEP_SUMMARY) {
      fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `\n\`\`\`json\n${line}\n\`\`\`\n`);
    }
  };
  if (process.argv[2] === 'report') {
    const path = process.env.FIREBASE_DEBUG_PATH;
    report(path && fs.existsSync(path) ? fs.readFileSync(path, 'utf8') : '', write);
    return;
  }
  if (process.argv[2] !== 'check') throw new Error('Use check or report.');
  const credentials = JSON.parse(fs.readFileSync(process.env.GOOGLE_APPLICATION_CREDENTIALS, 'utf8'));
  // Resolve the same auth library installed with the pinned Firebase CLI via NODE_PATH.
  const { GoogleAuth } = require('google-auth-library');
  const auth = new GoogleAuth({ keyFilename: process.env.GOOGLE_APPLICATION_CREDENTIALS,
    scopes: ['https://www.googleapis.com/auth/cloud-platform'] });
  await check({ credentials, appId: process.env.FIREBASE_APP_ID,
    packageName: process.env.ANDROID_APPLICATION_ID, request: options => auth.request(options), write });
}

if (require.main === module) {
  main().catch(() => {
    console.error('::error::Firebase diagnostics could not load service-account ADC or its auth library. No raw credential/auth error is printed.');
    process.exitCode = 1;
  });
}
module.exports = { check, errorDetails, report };
