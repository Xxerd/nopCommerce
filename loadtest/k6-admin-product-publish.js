/**
 * k6 load test — Admin Product Publish Flow
 *
 * Drives the instrumented flow:
 *   HTTP POST /Admin/Product/Create  →  ProductService  →  PostgreSQL  →  cache invalidation
 *
 * Usage:
 *   k6 run loadtest/k6-admin-product-publish.js
 *
 * Override defaults via env vars:
 *   k6 run -e BASE_URL=http://localhost:5000 \
 *           -e ADMIN_EMAIL=admin@yourStore.com \
 *           -e ADMIN_PASSWORD=admin \
 *           loadtest/k6-admin-product-publish.js
 *
 * Prerequisites:
 *   1. Observability stack running:  docker compose -f docker-compose.observability.yml up -d
 *   2. nopCommerce app running with OTLP export enabled (see README)
 *   3. k6 installed: https://k6.io/docs/getting-started/installation/
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Counter } from 'k6/metrics';

// ── Config ────────────────────────────────────────────────────────────────────

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const ADMIN_EMAIL = __ENV.ADMIN_EMAIL || 'andre@gmail.com';
const ADMIN_PASSWORD = __ENV.ADMIN_PASSWORD || 'Admin@123';

// ── Load profile ──────────────────────────────────────────────────────────────
//
// Three-stage ramp designed to produce clear signal in the dashboard:
//   - Ramp up:    0 → 5 VUs over 30 s
//   - Steady:     5 VUs for 2 min  (each VU creates ~1 product every 3-5 s)
//   - Ramp down:  5 → 0 VUs over 30 s
//
// Expected throughput: ~1-2 product publishes/s — enough to see counters and
// traces move in Grafana without flooding the database.

export const options = {
  stages: [
    { duration: '30s', target: 5 },
    { duration: '2m', target: 5 },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    http_req_failed: ['rate<0.10'],          // allow up to 10 % failures (form errors)
    http_req_duration: ['p(95)<10000'],        // p95 under 10 s
    'products_created': ['count>10'],          // at least 10 successful publishes
  },
};

// ── Custom metrics ────────────────────────────────────────────────────────────

const productsCreated = new Counter('products_created');
const createErrors = new Rate('create_errors');

// ── Helpers ───────────────────────────────────────────────────────────────────

/**
 * Extract the ASP.NET Core anti-forgery token from an HTML response body.
 * nopCommerce protects all state-changing forms with __RequestVerificationToken.
 */
function extractToken(html) {
  const match = html.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
  return match ? match[1] : null;
}

/**
 * Login to the nopCommerce admin panel.
 * The cookie jar is per-VU and persists across iterations, so this only
 * needs to run once per VU (on __ITER === 0).
 *
 * Returns true on success.
 */
function login() {
  const loginPage = http.get(`${BASE_URL}/login`, { tags: { name: 'login_page' } });

  const token = extractToken(loginPage.body);
  if (!token) {
    console.error('Could not extract CSRF token from login page');
    return false;
  }

  const res = http.post(
    `${BASE_URL}/login`,
    {
      Email: ADMIN_EMAIL,
      Password: ADMIN_PASSWORD,
      RememberMe: 'false',
      __RequestVerificationToken: token,
    },
    { redirects: 5, tags: { name: 'login_submit' } }
  );

  return check(res, {
    'admin login: status 200': (r) => r.status === 200,
    'admin login: not on login page': (r) => !r.url.endsWith('/login'),
  });
}

// ── Main scenario ─────────────────────────────────────────────────────────────

export default function () {

  // Login on every iteration — nopCommerce's admin session does not persist
  // reliably across k6 iterations due to anti-forgery token rotation on redirect.
  const ok = login();
  if (!ok) {
    console.error(`VU ${__VU} iter ${__ITER}: login failed, skipping`);
    sleep(2);
    return;
  }

  // ── Step 1: GET the product create form ─────────────────────────────────────

  const formPage = http.get(
    `${BASE_URL}/Admin/Product/Create`,
    { tags: { name: 'product_create_form' } }
  );

  const formOk = check(formPage, {
    'create form: status 200': (r) => r.status === 200,
    'create form: not redirected to login': (r) => !r.url.includes('/login'),
  });

  if (!formOk) {
    sleep(1);
    return;
  }

  const token = extractToken(formPage.body);
  if (!token) {
    console.error(`VU ${__VU} iter ${__ITER}: no CSRF token on create form`);
    return;
  }

  // Step 2: POST new product (Published = true)

  const productName = `LoadTest-VU${__VU}-I${__ITER}-${Date.now()}`;

  const createRes = http.post(
    `${BASE_URL}/Admin/Product/Create`,
    {
      __RequestVerificationToken: token,
      Name: productName,
      ProductTypeId: '5',       // SimpleProduct
      VisibleIndividually: 'true',
      Published: 'true',
      Price: '9.99',
      TaxCategoryId: '0',
      ManageInventoryMethodId: '0',       // DontManageStock
      StockQuantity: '0',
      OrderMinimumQuantity: '1',
      OrderMaximumQuantity: '10000',
    },
    { redirects: 5, tags: { name: 'product_create_submit' } }
  );

  const success = check(createRes, {
    'product created: 2xx': (r) => r.status >= 200 && r.status < 300,
    'product created: no 500': (r) => r.status !== 500,
  });

  if (success) {
    productsCreated.add(1);
    createErrors.add(0);
  } else {
    createErrors.add(1);
    console.warn(`VU ${__VU} iter ${__ITER}: create failed (status ${createRes.status})`);
  }


  sleep(Math.random() * 2 + 2);
}
