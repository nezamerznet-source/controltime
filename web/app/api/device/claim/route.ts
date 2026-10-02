import { NextRequest } from 'next/server';
import { claimSchema } from '@/lib/schema';
import { admin, ApiError, failure, hash, json, rateLimit, readJson } from '@/lib/server';
export async function POST(req:NextRequest){try{
  await rateLimit(req,'claim',20,600);const v=claimSchema.parse(await readJson(req,4096));
  const {data,error}=await admin().rpc('claim_family_device',{p_code_hash:hash(v.code),p_device_id:v.deviceId,p_token_hash:hash(v.token),p_name:v.name});
  if(error){if(error.code==='23505')throw new ApiError(409,'Код уже использован.');if(error.code==='22023')throw new ApiError(400,'Код истёк или недействителен. Создайте новый в кабинете.');throw new ApiError(503,'Не удалось подключить компьютер.');}
  return json({deviceId:data});
}catch(e){return failure(e);}}
