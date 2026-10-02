import { NextRequest } from 'next/server';
import { z } from 'zod';
import { admin, ApiError, checkOrigin, failure, json, parent, rateLimit } from '@/lib/server';
export async function DELETE(req:NextRequest,{params}:{params:Promise<{id:string}>}){try{
  checkOrigin(req);const {user}=await parent();const {id}=await params;z.uuid().parse(id);
  await rateLimit(req,'delete',20,600,user.id);
  const {data,error}=await admin().from('devices').delete().eq('id',id).eq('owner_id',user.id).select('id');
  if(error)throw new ApiError(503,'Не удалось удалить компьютер.');
  if(!data?.length)throw new ApiError(404,'Компьютер не найден.');
  return json({ok:true});
}catch(e){return failure(e);}}
