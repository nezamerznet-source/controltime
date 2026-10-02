import { z } from 'zod';
const clean = (length: number) => z.string().min(1).max(length).refine(v => !/[\u0000-\u001f\u007f]/.test(v));
const seconds = z.number().finite().min(0).max(100_000);
export const dayString = z.string().regex(/^\d{4}-\d{2}-\d{2}$/).refine(v => {
  const d = new Date(v + 'T12:00:00Z'); return Number.isFinite(+d) && d.toISOString().slice(0,10) === v;
});
export const usageSchema = z.object({
  key: clean(400), name: clean(253), category: clean(60), seconds, background: z.boolean().optional()
}).strict();
export const sessionSchema = z.object({
  start: z.iso.datetime({ offset: true }), end: z.iso.datetime({ offset: true }), activeSeconds: seconds,
  activities: z.array(usageSchema).max(5000)
}).strict().refine(v => +new Date(v.end) >= +new Date(v.start) && v.activeSeconds <= (+new Date(v.end)-+new Date(v.start))/1000 + 1 && Math.abs(v.activities.reduce((s,a)=>s+a.seconds,0)-v.activeSeconds)<1);
export const daySchema = z.object({
  day: dayString, revision: z.number().int().positive().max(Number.MAX_SAFE_INTEGER),
  activeSeconds: seconds, idleSeconds: seconds, unknownSeconds: seconds, backgroundSeconds: seconds, browserUnknownSeconds: seconds,
  activities: z.array(usageSchema).max(5000), categories: z.array(usageSchema).max(100),
  sessions: z.array(sessionSchema).max(1500).nullable()
}).strict().refine(v => [v.activities,v.categories].every(rows => Math.abs(rows.reduce((sum,r)=>sum+r.seconds,0)-v.activeSeconds)<1), 'Totals must reconcile');
export const metadataSchema = z.object({
  profileName: clean(60), timeZone: clean(100).refine(v => { try { new Intl.DateTimeFormat('en',{timeZone:v});return true; } catch{return false;} }),
  limitMinutes: z.number().int().min(1).max(1440), limitEnabled:z.boolean(), paused:z.boolean(),
  lastObservedAt:z.iso.datetime({offset:true}).nullable(), browserConnected:z.boolean(),
  historyDays:z.number().int().min(7).max(365), summaryDays:z.number().int().min(7).max(730)
}).strict();
export const syncSchema = z.object({protocol:z.literal(1),metadata:metadataSchema,days:z.array(daySchema).max(7)}).strict();
export const claimSchema = z.object({code:z.string().regex(/^[A-Z2-9]{10}$/),deviceId:z.uuid(),token:z.string().regex(/^[A-Za-z0-9_-]{43}$/),name:clean(100)}).strict();
export const credentialsSchema = z.object({email:z.email().max(254),password:z.string().min(8).max(128)}).strict();
export type Usage = z.infer<typeof usageSchema>;
export type DaySnapshot = z.infer<typeof daySchema>;
export type DeviceMetadata = z.infer<typeof metadataSchema>;
export type Device = {id:string;name:string;created_at:string;last_seen_at:string|null;revoked_at:string|null;metadata:Partial<DeviceMetadata>};
export type DashboardData = {user:{email:string};devices:Device[];selectedDeviceId:string|null;days:DaySnapshot[];today:string;from:string;to:string};
