import { test, expect, type Page } from '@playwright/test';
const device={id:'11111111-1111-4111-8111-111111111111',name:'HOME-PC',created_at:'2026-10-01T12:00:00Z',last_seen_at:'2026-10-02T15:00:00Z',revoked_at:null,metadata:{profileName:'Мирон',timeZone:'Europe/Moscow',limitEnabled:true,limitMinutes:180,lastObservedAt:'2026-10-02T15:00:00Z',browserConnected:true,historyDays:90,summaryDays:365}};
const usage=[{key:'app:roblox',name:'Roblox',category:'Игры',seconds:3600},{key:'site:uchi.ru',name:'uchi.ru',category:'Учёба',seconds:1800},{key:'site:youtube.com',name:'youtube.com',category:'Видео',seconds:2400}];
const days=Array.from({length:7},(_,i)=>({day:i<5?'2026-09-'+(26+i):'2026-10-0'+(i-4),revision:1,activeSeconds:7800,idleSeconds:600,unknownSeconds:0,backgroundSeconds:120,browserUnknownSeconds:0,activities:usage,categories:usage.map(u=>({...u,key:u.category,name:u.category})),sessions:[{start:'2026-10-02T12:00:00Z',end:'2026-10-02T14:10:00Z',activeSeconds:7800,activities:usage}]}));
async function mock(page:Page,hasDevice=true){
  await page.route('**/api/session',r=>r.fulfill({json:{configured:true,user:{email:'parent@example.com'}}}));
  await page.route('**/api/dashboard?*',r=>{const q=new URL(r.request().url()).searchParams;const from=q.get('from')??'2026-09-26',to=q.get('to')??'2026-10-02';return r.fulfill({json:{user:{email:'parent@example.com'},devices:hasDevice?[device]:[],selectedDeviceId:hasDevice?device.id:null,days:hasDevice?days.filter(d=>d.day>=from&&d.day<=to):[],today:'2026-10-02',from,to}});});
  await page.route('**/api/devices',r=>r.fulfill({json:{code:'ABCDE-23456',expiresAt:new Date(Date.now()+600000).toISOString()}}));
}
async function noOverflow(page:Page){expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();}
test('dashboard, history, device connection and responsive layout',async({page},info)=>{
  const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));
  await mock(page);await page.goto('/');await expect(page.getByRole('heading',{name:'Как проходит день'})).toBeVisible();
  await expect(page.locator('.big-time')).toHaveText('2 ч 10 мин');await noOverflow(page);
  await page.screenshot({path:info.outputPath('overview.png'),fullPage:true});
  await page.getByRole('button',{name:'2 октября: 2 ч 10 мин. Открыть историю.',exact:true}).click();
  await expect(page.getByRole('heading',{name:'История активности'})).toBeVisible();await page.locator('.session summary').click();await expect(page.locator('.session-inner').getByText('Roblox')).toBeVisible();await noOverflow(page);
  await page.screenshot({path:info.outputPath('history.png'),fullPage:true});
  await page.getByRole('button',{name:/^Компьютеры/}).filter({visible:true}).click();await expect(page.getByRole('heading',{name:'Ваши компьютеры'})).toBeVisible();
  await page.getByRole('button',{name:'Подключить компьютер',exact:true}).click();await expect(page.getByRole('dialog')).toBeVisible();await page.getByRole('button',{name:'Получить код подключения'}).click();await expect(page.getByText('ABCDE-23456')).toBeVisible();await noOverflow(page);await page.screenshot({path:info.outputPath('pairing.png'),fullPage:true});
  await page.getByRole('button',{name:'Закрыть',exact:true}).click();expect(errors).toEqual([]);
});
test('empty account does not show fabricated statistics',async({page},info)=>{
  await mock(page,false);await page.goto('/');await expect(page.getByRole('heading',{name:'Начнём с вашего компьютера'})).toBeVisible();await expect(page.locator('.big-time')).toHaveCount(0);await noOverflow(page);await page.screenshot({path:info.outputPath('empty.png'),fullPage:true});
});
test('login, registration and password reset submit the selected action',async({page},info)=>{
  await page.route('**/api/session',r=>r.fulfill({json:{configured:true,user:null}}));
  const actions:string[]=[];await page.route('**/api/auth',r=>{actions.push(r.request().postDataJSON().action);return r.fulfill({json:{message:'Проверьте почту'}});});
  await page.goto('/');await page.getByLabel('Электронная почта').fill('parent@example.com');await page.getByLabel('Пароль',{exact:true}).fill('test-password');await page.getByRole('button',{name:'Войти в кабинет'}).click();await expect(page.getByRole('status')).toHaveText('Проверьте почту');
  await page.getByRole('button',{name:'Создать аккаунт',exact:true}).first().click();await page.getByRole('button',{name:'Создать аккаунт',exact:true}).last().click();await expect.poll(()=>actions.length).toBe(2);
  await page.getByRole('button',{name:'Войти',exact:true}).click();await page.getByRole('button',{name:'Не помню пароль'}).click();await page.getByRole('button',{name:'Отправить письмо'}).click();await expect.poll(()=>actions).toEqual(['sign-in','sign-up','reset']);await noOverflow(page);
  await page.getByRole('button',{name:'Вернуться ко входу'}).click();await page.screenshot({path:info.outputPath('login.png'),fullPage:true});
});
