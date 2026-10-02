import { NextRequest } from 'next/server';
import { z } from 'zod';
import { credentialsSchema } from '@/lib/schema';
import { ApiError, checkOrigin, failure, json, rateLimit, readJson, sessionClient, siteUrl } from '@/lib/server';
export async function POST(req:NextRequest){try{
  checkOrigin(req);const body=await readJson(req,4096);
  const action=z.enum(['sign-in','sign-up','sign-out','reset','password']).parse(body.action);
  await rateLimit(req,'auth',30,600);
  const client=await sessionClient();
  if(action==='sign-out'){const {error}=await client.auth.signOut();if(error)throw new ApiError(503,'Не удалось выйти. Повторите попытку.');return json({ok:true});}
  if(action==='reset'){
    const email=z.email().max(254).parse(body.email);
    await client.auth.resetPasswordForEmail(email,{redirectTo:siteUrl()+'/auth/callback?next=password'});
    return json({message:'Если такой адрес зарегистрирован, на него придёт письмо для смены пароля.'});
  }
  if(action==='password'){
    const password=z.string().min(8).max(128).parse(body.password);
    const {data:{user}}=await client.auth.getUser();if(!user)throw new ApiError(401,'Откройте ссылку из письма ещё раз.');
    const {error}=await client.auth.updateUser({password});if(error)throw new ApiError(400,'Не удалось изменить пароль. Попробуйте другой пароль или запросите новую ссылку.');
    return json({ok:true});
  }
  const input=credentialsSchema.parse({email:body.email,password:body.password});
  if(action==='sign-up'){
    const {data,error}=await client.auth.signUp({...input,options:{emailRedirectTo:siteUrl()+'/auth/callback'}});
    if(error)throw new ApiError(400,'Не удалось зарегистрироваться. Проверьте адрес, используйте надёжный пароль и попробуйте позже.');
    return json(data.session?{ok:true}:{message:'Проверьте почту: отправили ссылку для подтверждения адреса. Если аккаунт уже есть, войдите в него.'});
  }
  const {error}=await client.auth.signInWithPassword(input);
  if(error)throw new ApiError(401,'Не удалось войти. Проверьте почту, пароль и подтверждение адреса.');
  return json({ok:true});
}catch(e){return failure(e);}}
