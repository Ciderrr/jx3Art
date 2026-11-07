import {defineConfig} from 'vite';
import react from '@vitejs/plugin-react';
export default defineConfig({plugins:[react()],base:'./',build:{outDir:'../JX3Artist/web',emptyOutDir:true,target:'es2022'},server:{host:'127.0.0.1',port:5187,strictPort:true}});
