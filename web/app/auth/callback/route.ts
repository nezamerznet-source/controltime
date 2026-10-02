import { NextRequest, NextResponse } from 'next/server';
import { sessionClient, siteUrl } from '@/lib/server';
export async function GET(req:NextRequest){
  const code=req.nextUrl.searchParams.get('code');
  try{if(code){const {error}=await(await sessionClient()).auth.exchangeCodeForSession(code);if(!error)return NextResponse.redirect(siteUrl()+(req.nextUrl.searchParams.get('next')==='password'?'/?password=1':'/'));}}
  catch{}
  return NextResponse.redirect(new URL('/?authError=1',siteUrl()));
}
