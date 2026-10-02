import { test } from 'node:test';
import assert from 'node:assert/strict';
import { activities, dayRange, shiftDay, todayInZone } from '../lib/data';
import { daySchema, syncSchema, claimSchema, type DaySnapshot } from '../lib/schema';
export const row={key:'app:game',name:'Игра',category:'Игры',seconds:60,background:false};
export const day:DaySnapshot={day:'2026-10-02',revision:2,activeSeconds:60,idleSeconds:0,unknownSeconds:0,backgroundSeconds:0,browserUnknownSeconds:0,activities:[row],categories:[{...row,key:'Игры',name:'Игры'}],sessions:[{start:'2026-10-02T12:00:00+00:00',end:'2026-10-02T12:01:00+00:00',activeSeconds:60,activities:[row]}]};
export const metadata={profileName:'Мирон',timeZone:'Europe/Moscow',limitMinutes:120,limitEnabled:true,paused:false,lastObservedAt:'2026-10-02T12:01:00+00:00',browserConnected:false,historyDays:90,summaryDays:365};
test('calendar days handle month, leap year and device timezone',()=>{
  assert.equal(shiftDay('2026-03-01',-1),'2026-02-28');assert.equal(shiftDay('2024-03-01',-1),'2024-02-29');
  assert.deepEqual(dayRange('2026-09-30','2026-10-02'),['2026-09-30','2026-10-01','2026-10-02']);
  assert.equal(todayInZone('Europe/Moscow',new Date('2026-10-02T22:00:00Z')),'2026-10-03');
});
test('Windows wire payload is valid and totals are not double counted',()=>{
  assert.ok(syncSchema.safeParse({protocol:1,metadata,days:[day]}).success);
  assert.equal(activities([day,{...day,day:'2026-10-03'}])[0].seconds,120);
  assert.ok(!daySchema.safeParse({...day,activeSeconds:120}).success);
  assert.ok(!daySchema.safeParse({...day,day:'2026-02-30'}).success);
  assert.ok(!daySchema.safeParse({...day,sessions:[{...day.sessions![0],activeSeconds:900}]}).success);
});
test('protocol rejects unbounded payloads, extra data, malformed secrets and unknown timezones',()=>{
  assert.ok(!syncSchema.safeParse({protocol:1,metadata:{...metadata,timeZone:'bad'},days:[]}).success);
  assert.ok(!daySchema.safeParse({...day,screenshot:'private'}).success);
  assert.ok(!daySchema.safeParse({...day,revision:1.5}).success);
  assert.ok(!claimSchema.safeParse({code:'short',deviceId:'x',token:'password',name:'PC'}).success);
  assert.ok(claimSchema.safeParse({code:'ABCDE23456',deviceId:'11111111-1111-4111-8111-111111111111',token:'a'.repeat(43),name:'PC'}).success);
});
