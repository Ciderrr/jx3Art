import { readFile, writeFile } from 'node:fs/promises';
import { Resvg } from '../frontend/node_modules/@resvg/resvg-js/index.js';

const dir = new URL('./', import.meta.url);
const icon = await readFile(new URL('../frontend/public/app-icon.svg', dir));
const artwork = new Resvg(icon, { fitTo: { mode: 'width', value: 240 } }).render().asPng();
const cloud = `data:image/png;base64,${artwork.toString('base64')}`;
const banner = `<svg xmlns="http://www.w3.org/2000/svg" width="328" height="628" viewBox="0 0 164 314">
  <rect width="164" height="314" fill="#f4eee7"/>
  <path d="M0 0H164V75C110 57 51 54 0 84Z" fill="#e6d5c7"/>
  <path d="M0 230C59 199 103 234 164 206V314H0Z" fill="#dfcbb9"/>
  <path d="M0 256C72 215 111 259 164 236V314H0Z" fill="#b79681"/>
  <path d="M0 282C54 244 110 277 164 257V314H0Z" fill="#805b49"/>
  <image x="30" y="98" width="104" height="104" href="${cloud}"/>
</svg>`;
await writeFile(new URL('banner.png', dir), new Resvg(banner).render().asPng());
const dark = banner.replaceAll('#f4eee7', '#2a2724').replaceAll('#e6d5c7', '#39322c').replaceAll('#dfcbb9', '#4b3d33').replaceAll('#b79681', '#694e41').replaceAll('#805b49', '#93705d').replaceAll('#fffaf4', '#443a33');
await writeFile(new URL('banner-dark.png', dir), new Resvg(dark).render().asPng());
await writeFile(new URL('cloud-sleeve.png', dir), new Resvg(icon, { fitTo: { mode: 'width', value: 128 } }).render().asPng());
