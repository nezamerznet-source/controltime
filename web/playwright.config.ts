import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir:'./e2e',fullyParallel:true,timeout:30_000,retries:process.env.CI?1:0,
  use:{baseURL:'http://127.0.0.1:3100',trace:'retain-on-failure'},
  webServer:{command:'npm run start -- --port 3100',url:'http://127.0.0.1:3100',reuseExistingServer:!process.env.CI,timeout:60_000},
  projects:[{name:'desktop',use:{...devices['Desktop Chrome'],viewport:{width:1440,height:1000}}},{name:'mobile',use:{...devices['iPhone 13'],defaultBrowserType:'chromium'}}]
});
