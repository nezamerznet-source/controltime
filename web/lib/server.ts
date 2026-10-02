import 'server-only';
import { createServerClient } from '@supabase/ssr';
import { createClient } from '@supabase/supabase-js';
import { cookies } from 'next/headers';
import { NextRequest, NextResponse } from 'next/server';
import { createHash, createHmac } from 'node:crypto';
import { ZodError } from 'zod';

export class ApiError extends Error { constructor(public status:number,message:string){super(message);} }
export function configured(){return !!(process.env.SUPABASE_URL && process.env.SUPABASE_ANON_KEY && process.env.SUPABASE_SERVICE_ROLE_KEY && process.env.SITE_URL);}
function env(name:string){const value=process.env[name];if(!value)throw new ApiError(503,'Кабинет ещё не подключён. Попробуйте позже.');return value;}
export function siteUrl(){return env('SITE_URL').replace(/\/$/,'');}
export function admin(){return createClient(env('SUPABASE_URL'),env('SUPABASE_SERVICE_ROLE_KEY'),{auth:{persistSession:false,autoRefreshToken:false}});}
export async function sessionClient(){
  const jar=await cookies();
  return createServerClient(env('SUPABASE_URL'),env('SUPABASE_ANON_KEY'),{
    cookieOptions:{httpOnly:true,secure:new URL(siteUrl()).protocol==='https:',sameSite:'lax',path:'/'},
    cookies:{getAll(){return jar.getAll();},setAll(items){items.forEach(({name,value,options})=>jar.set(name,value,options));}}
  });
}
export async function parent(){
  const client=await sessionClient();const {data:{user},error}=await client.auth.getUser();
  if(error||!user)throw new ApiError(401,'Войдите в личный кабинет.');
  return {client,user};
}
export function hash(value:string){return createHash('sha256').update(value).digest('hex');}
export function checkOrigin(req:NextRequest){
  if(req.headers.get('origin')!==new URL(siteUrl()).origin)throw new ApiError(403,'Откройте кабинет по его основному адресу.');
}
export async function readJson(req:NextRequest,max=1_500_000){
  if(!(req.headers.get('content-type')??'').startsWith('application/json'))throw new ApiError(415,'Ожидается JSON.');
  if(Number(req.headers.get('content-length')??0)>max)throw new ApiError(413,'Слишком большой запрос.');
  const reader=req.body?.getReader();if(!reader)throw new ApiError(400,'Пустой запрос.');
  const chunks:Uint8Array[]=[];let size=0;
  try{while(true){const {done,value}=await reader.read();if(done)break;size+=value.length;if(size>max){await reader.cancel();throw new ApiError(413,'Слишком большой запрос.');}chunks.push(value);}}
  finally{reader.releaseLock();}
  try{return JSON.parse(Buffer.concat(chunks).toString('utf8'));}catch{throw new ApiError(400,'Неверный формат запроса.');}
}
export async function rateLimit(req:NextRequest,scope:string,limit:number,seconds:number,identity?:string){
  const ip=req.headers.get('x-real-ip')??req.headers.get('x-forwarded-for')?.split(',')[0]?.trim()??'unknown';
  const key=createHmac('sha256',env('SUPABASE_SERVICE_ROLE_KEY')).update(scope+':'+(identity??ip)).digest('hex');
  const {data,error}=await admin().rpc('consume_request_limit',{p_key:key,p_limit:limit,p_seconds:seconds});
  if(error)throw new ApiError(503,'Сервис временно недоступен.');
  if(!data)throw new ApiError(429,'Слишком много попыток. Подождите несколько минут.');
}
export function bearer(req:NextRequest){
  const value=req.headers.get('authorization')??'';
  if(!/^Bearer [A-Za-z0-9_-]{43}$/.test(value))throw new ApiError(401,'Устройство не подключено.');
  return value.slice(7);
}
export function json(value:unknown,status=200){return NextResponse.json(value,{status,headers:{'Cache-Control':'private, no-store'}});}
export function failure(error:unknown){
  if(error instanceof ApiError)return json({error:error.message},error.status);
  if(error instanceof ZodError)return json({error:'Проверьте введённые данные.'},400);
  // Never print request bodies, access tokens, database errors or family activity.
  console.error('Family Time: request failed');
  return json({error:'Не удалось выполнить запрос. Попробуйте ещё раз.'},500);
}
