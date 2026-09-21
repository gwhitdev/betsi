import { copyFile, mkdir } from 'node:fs/promises';
// Kept in source so `dotnet run` and Docker builds need no Node installation or CDN.
await mkdir('Betsi/wwwroot/lib/signalr', { recursive: true });
await copyFile('node_modules/@microsoft/signalr/dist/browser/signalr.min.js', 'Betsi/wwwroot/lib/signalr/signalr.min.js');
await copyFile('node_modules/@microsoft/signalr/dist/browser/signalr.min.js.map', 'Betsi/wwwroot/lib/signalr/signalr.min.js.map');
// The npm package omits the license file; the upstream MIT license is retained beside the bundle.
