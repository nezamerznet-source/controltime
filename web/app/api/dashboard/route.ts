import { NextRequest } from 'next/server';
import { z } from 'zod';
import { dayString } from '@/lib/schema';
import type { Device } from '@/lib/schema';
import { dayRange, shiftDay, todayInZone } from '@/lib/data';
import { ApiError, failure, json, parent } from '@/lib/server';
export async function GET(req:NextRequest){try{
  const {client,user}=await parent();
  const {data:rows,error}=await client.from('devices').select('id,name,created_at,last_seen_at,revoked_at,metadata').is('revoked_at',null).order('created_at');
  if(error)throw new ApiError(503,'Не удалось загрузить компьютеры.');
  const devices=(rows??[]) as Device[];const requested=req.nextUrl.searchParams.get('device');
  if(requested)z.uuid().parse(requested);
  const selected=requested?devices.find(d=>d.id===requested):devices[0];
  if(requested&&!selected)throw new ApiError(404,'Компьютер не найден.');
  const today=todayInZone(selected?.metadata.timeZone);
  const to=dayString.parse(req.nextUrl.searchParams.get('to')??today);
  const from=dayString.parse(req.nextUrl.searchParams.get('from')??shiftDay(to,-6));
  if(from>to||to>today||dayRange(from,to).length>366)throw new ApiError(400,'Выберите период до года, без будущих дат.');
  let days=[];
  if(selected){const {data,error}=await client.from('daily_snapshots').select('payload').eq('device_id',selected.id).gte('day',from).lte('day',to).order('day');if(error)throw new ApiError(503,'Не удалось загрузить историю.');days=(data??[]).map(d=>d.payload);}
  return json({user:{email:user.email},devices,selectedDeviceId:selected?.id??null,days,today,from,to});
}catch(e){return failure(e);}}
