import type { DaySnapshot, Usage } from './schema';
export function todayInZone(zone='Europe/Moscow',now=new Date()){
  const p=new Intl.DateTimeFormat('en-CA',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(now);
  return ['year','month','day'].map(k=>p.find(x=>x.type===k)!.value).join('-');
}
export function shiftDay(day:string,amount:number){const d=new Date(day+'T12:00:00Z');d.setUTCDate(d.getUTCDate()+amount);return d.toISOString().slice(0,10);}
export function dayRange(from:string,to:string){const result:string[]=[];for(let d=from;d<=to&&result.length<731;d=shiftDay(d,1))result.push(d);return result;}
export function duration(seconds:number){const m=Math.floor(seconds/60);return m>=60?`${Math.floor(m/60)} ч ${m%60} мин`:`${m} мин`;}
export function dateLabel(day:string,short=false){return new Intl.DateTimeFormat('ru',{day:'numeric',month:short?'short':'long',timeZone:'UTC'}).format(new Date(day+'T12:00:00Z'));}
export function clock(value:string,zone:string){return new Intl.DateTimeFormat('ru',{hour:'2-digit',minute:'2-digit',timeZone:zone}).format(new Date(value));}
export function combine(rows:Usage[]){const map=new Map<string,Usage>();for(const row of rows){const p=map.get(row.key);map.set(row.key,{...row,seconds:(p?.seconds??0)+row.seconds});}return [...map.values()].sort((a,b)=>b.seconds-a.seconds);}
export function activities(days:DaySnapshot[]){return combine(days.flatMap(d=>d.activities));}
export const palette=['#5278f5','#9a83ee','#f4ab58','#65b99c','#dc7da6','#8093aa'];
export function categoryColor(name:string){const known:Record<string,string>={'Учёба':palette[0],'Игры':palette[1],'Видео':palette[2],'Общение':palette[3],'Другое':palette[5]};return known[name]??palette[[...name].reduce((n,c)=>n+c.charCodeAt(0),0)%palette.length];}
