import type { Metadata, Viewport } from 'next';
import './globals.css';
export const metadata:Metadata={title:'Family Time — время для важного',description:'Личный кабинет семьи: время за компьютером, приложения и история сеансов.',robots:{index:false,follow:false}};
export const viewport:Viewport={width:'device-width',initialScale:1,themeColor:'#f6f8fc'};
export default function Layout({children}:{children:React.ReactNode}){return <html lang="ru"><body>{children}</body></html>;}
