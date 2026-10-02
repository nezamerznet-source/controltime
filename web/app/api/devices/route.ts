import { NextRequest } from 'next/server';
import { randomInt } from 'node:crypto';
import { admin, ApiError, checkOrigin, failure, hash, json, parent, rateLimit } from '@/lib/server';
export async function POST(req:NextRequest){try{
  checkOrigin(req);const {user}=await parent();await rateLimit(req,'pair',10,600,user.id);
  const db=admin();
  const {count,error:countError}=await db.from('devices').select('id',{count:'exact',head:true}).eq('owner_id',user.id);
  if(countError)throw new ApiError(503,'Не удалось создать код.');
  if((count??0)>=20)throw new ApiError(400,'Можно подключить до 20 компьютеров. Удалите ненужный.');
  const alphabet='ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  const code=Array.from({length:10},()=>alphabet[randomInt(alphabet.length)]).join('');
  const expires=new Date(Date.now()+600_000).toISOString();
  await db.from('pairing_codes').delete().eq('owner_id',user.id).is('consumed_at',null);
  const {error}=await db.from('pairing_codes').insert({owner_id:user.id,code_hash:hash(code),expires_at:expires});
  if(error)throw new ApiError(503,'Не удалось создать код. Попробуйте ещё раз.');
  await db.from('request_limits').delete().lt('window_start',new Date(Date.now()-2*86400_000).toISOString());
  await db.from('pairing_codes').delete().lt('expires_at',new Date(Date.now()-86400_000).toISOString());
  return json({code:code.slice(0,5)+'-'+code.slice(5),expiresAt:expires});
}catch(e){return failure(e);}}
