/**
 * k6 demo script — "Admin Product Publish" story arc
 *
 * Designed for a live dashboard demo. Produces four distinct phases
 * that tell a coherent observability story:
 *
 *   Phase 1 — Healthy baseline   (0 → 1m)
 *     1 req/s arrival rate. Steady product creation.
 *     Dashboard shows: stable publish rate ~1/s, DB p95 low, cache clear fast.
 *
 *   Phase 2 — Request rate spike  (1m → 2m30s)
 *     Ramps to 8 req/s. More requests arrive than the DB can absorb at baseline
 *     latency → requests queue → save_duration p95 climbs.
 *     Story: "Request rate doubled — DB is feeling it."
 *
 *   Phase 3 — Recovery            (2m30s → 3m30s)
 *     Back to 1 req/s. Queue drains, latency returns to baseline.
 *     Story: "Traffic dropped — system recovered."
 *
 *   Phase 4 — Validation errors   (3m30s → 4m30s)
 *     A few VUs submit forms with no Name (required field).
 *     Dashboard shows: validation_failures counter appears while HTTP error
 *     rate stays at 0%.
 *     Story: "Silent failures — HTTP error rate stays 0% but the form is broken."
 *
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate } from 'k6/metrics';

// ── Config ────────────────────────────────────────────────────────────────────

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const ADMIN_EMAIL = __ENV.ADMIN_EMAIL || 'andre@gmail.com';
const ADMIN_PASSWORD = __ENV.ADMIN_PASSWORD || 'Admin@123';

// ── Load profile — four phases ────────────────────────────────────────────────

export const options = {
  scenarios: {

    // Phases 1–3: normal → spike → recovery
    // Uses ramping-arrival-rate: controls requests/s directly.
    // k6 allocates VUs automatically to meet the target rate.
    // When rate > DB throughput, requests queue → save_duration rises.
    product_creates: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: 10,
      maxVUs: 30,
      stages: [
        { duration: '20s', target: 1 },  // Phase 1 ramp-up
        { duration: '40s', target: 1 },  // Phase 1 steady  — 1 req/s baseline
        { duration: '15s', target: 8 },  // Phase 2 ramp-up — rate spike
        { duration: '55s', target: 8 },  // Phase 2 steady  — 8 req/s, DB under pressure
        { duration: '15s', target: 1 },  // Phase 3 ramp-down — rate drops
        { duration: '45s', target: 1 },  // Phase 3 steady  — recovery
        { duration: '10s', target: 0 },  // final ramp-down
      ],
      exec: 'createProduct',
    },

    // Phase 4: validation failures — starts at 3m30s, runs for 1 minute
    // A small number of VUs submit forms without the required Name field.
    // Returns HTTP 200 with inline form errors — invisible to error-rate metrics.
    validation_failures: {
      executor: 'constant-vus',
      vus: 2,
      duration: '1m',
      startTime: '3m30s',
      exec: 'submitInvalidForm',
    },

  },

  thresholds: {
    http_req_failed: ['rate<0.15'],
    http_req_duration: ['p(95)<15000'],
  },
};

// Custom metrics 

const productsCreated = new Counter('products_created');
const validationAttempts = new Counter('validation_failure_attempts');

// Helpers 

function extractToken(html) {
  const match = html.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
  return match ? match[1] : null;
}

function login() {
  const page = http.get(`${BASE_URL}/login`, { tags: { name: 'login_page' } });
  const token = extractToken(page.body);
  if (!token) return false;

  const res = http.post(`${BASE_URL}/login`, {
    Email: ADMIN_EMAIL,
    Password: ADMIN_PASSWORD,
    RememberMe: 'false',
    __RequestVerificationToken: token,
  }, { redirects: 5, tags: { name: 'login_submit' } });

  return check(res, {
    'login ok': (r) => r.status === 200 && !r.url.endsWith('/login'),
  });
}

// Scenario 1: normal product creation

export function createProduct() {
  if (!login()) { sleep(2); return; }

  const formPage = http.get(`${BASE_URL}/Admin/Product/Create`,
    { tags: { name: 'product_create_form' } });

  if (!check(formPage, { 'form ok': (r) => r.status === 200 && !r.url.includes('/login') })) {
    sleep(1); return;
  }

  const token = extractToken(formPage.body);
  if (!token) return;

  const res = http.post(`${BASE_URL}/Admin/Product/Create`, {
    __RequestVerificationToken: token,
    Name: `Story-VU${__VU}-${__ITER}-${Date.now()}`,
    ProductTypeId: '5',
    VisibleIndividually: 'true',
    Published: 'true',
    Price: '9.99',
    TaxCategoryId: '0',
    ManageInventoryMethodId: '0',
    StockQuantity: '0',
    OrderMinimumQuantity: '1',
    OrderMaximumQuantity: '10000',
  }, { redirects: 5, tags: { name: 'product_create_submit' } });

  if (check(res, {
    'product created': (r) => r.status >= 200 && r.status < 300,
    'no server error': (r) => r.status !== 500,
  })) {
    productsCreated.add(1);
  }

  // No sleep — ramping-arrival-rate controls the pace.
  // The executor starts a new iteration at the configured rate regardless,
  // so sleeping here would only waste a VU slot without reducing throughput.
}

// Scenario 2: submit invalid form (Phase 4) 

export function submitInvalidForm() {
  if (!login()) { sleep(2); return; }

  const formPage = http.get(`${BASE_URL}/Admin/Product/Create`,
    { tags: { name: 'invalid_form_page' } });

  if (!check(formPage, { 'form ok': (r) => r.status === 200 && !r.url.includes('/login') })) {
    sleep(1); return;
  }

  const token = extractToken(formPage.body);
  if (!token) return;

  // Deliberately omit Name — required field — to trigger server-side validation failure.
  const res = http.post(`${BASE_URL}/Admin/Product/Create`, {
    __RequestVerificationToken: token,
    // Name intentionally missing
    ProductTypeId: '5',
    VisibleIndividually: 'true',
    Published: 'true',
    Price: '9.99',
    TaxCategoryId: '0',
    ManageInventoryMethodId: '0',
    StockQuantity: '0',
    OrderMinimumQuantity: '1',
    OrderMaximumQuantity: '10000',
  }, { redirects: 5, tags: { name: 'invalid_form_submit' } });

  // The server returns 200 with form errors — HTTP metrics won't flag this.
  // Only nopcommerce_catalog_product_validation_failures_total will show it.
  check(res, {
    'returns 200 (silent failure)': (r) => r.status === 200,
    'not a server error': (r) => r.status !== 500,
  });

  validationAttempts.add(1);

  sleep(Math.random() * 2 + 1);
}
