import { configured, failure, json, sessionClient } from '@/lib/server';
export async function GET(){try{
  if(!configured())return json({configured:false,user:null});
  const {data:{user}}=await(await sessionClient()).auth.getUser();
  return json({configured:true,user:user?{email:user.email}:null});
}catch(e){return failure(e);}}
