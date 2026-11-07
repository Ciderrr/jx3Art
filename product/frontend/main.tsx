import React from 'react';import{createRoot}from'react-dom/client';import{ArtistApp}from'./ArtistApp';import{NativeBridge}from'./bridge';
const root=createRoot(document.getElementById('root')!);const webview=window.chrome?.webview;
if(webview){const bridge=new NativeBridge(webview);window.addEventListener('error',()=>void bridge.request('uiFailure').catch(()=>{}));window.addEventListener('unhandledrejection',()=>void bridge.request('uiFailure').catch(()=>{}));root.render(<ArtistApp bridge={bridge}/>);}else root.render(<p>请从艺人助手打开此界面。</p>);
