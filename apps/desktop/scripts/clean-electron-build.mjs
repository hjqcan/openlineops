import { rm } from 'node:fs/promises';

await rm(new URL('../dist-electron', import.meta.url), {
  force: true,
  recursive: true
});
