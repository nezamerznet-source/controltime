import { NextRequest } from 'next/server';
import { syncSchema } from '@/lib/schema';
import { admin, ApiError, bearer, failure, hash, json, rateLimit, readJson } from '@/lib/server';
export async function POST(req:NextRequest){try{
  const tokenHash=hash(bearer(req));await rateLimit(req,'sync',180,60,tokenHash);
  const v=syncSchema.parse(await readJson(req));
  const {data,error}=await admin().rpc('sync_family_device',{p_token_hash:tokenHash,p_metadata:v.metadata,p_days:v.days});
  if(error){if(error.code==='42501')throw new ApiError(401,'Доступ устройства отозван.');throw new ApiError(503,'Не удалось сохранить историю.');}
  return json(data);
}catch(e){return failure(e);}}
