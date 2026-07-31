import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { createHash } from 'node:crypto';

export default defineConfig(({ command }) => {
  const rendererNonce = process.env.OPENLINEOPS_RENDERER_NONCE;
  const rendererPort = command === 'serve'
    ? parseRendererPort(process.env.OPENLINEOPS_RENDERER_PORT)
    : 0;
  if (command === 'serve' && rendererNonce === undefined) {
    throw new Error('Vite renderer serving requires the Electron launch nonce.');
  }
  const rendererHeaders = rendererNonce === undefined
    ? undefined
    : {
        'X-OpenLineOps-Renderer-Proof': createRendererProof(rendererNonce),
        'Cache-Control': 'no-store'
      };

  return {
    base: './',
    plugins: [react()],
    build: {
      chunkSizeWarningLimit: 900,
      rollupOptions: {
        output: {
          manualChunks(id: string) {
            if (id.includes('node_modules/blockly')) {
              return 'blockly';
            }

            return undefined;
          }
        }
      }
    },
    server: {
      host: '127.0.0.1',
      port: rendererPort,
      strictPort: true,
      headers: rendererHeaders
    },
    preview: {
      host: '127.0.0.1',
      port: rendererPort,
      strictPort: true,
      headers: rendererHeaders
    }
  };
});

function createRendererProof(nonce: string): string {
  if (!/^[A-Za-z0-9_-]{43}$/u.test(nonce)) {
    throw new Error('OPENLINEOPS_RENDERER_NONCE must encode exactly 256 bits as base64url.');
  }

  return createHash('sha256')
    .update('OpenLineOps renderer startup\0', 'utf8')
    .update(nonce, 'utf8')
    .digest('base64url');
}

function parseRendererPort(value: string | undefined): number {
  if (value === undefined) {
    throw new Error('OPENLINEOPS_RENDERER_PORT must be assigned by the desktop launcher.');
  }
  if (!/^[1-9][0-9]{0,4}$/u.test(value)) {
    throw new Error('OPENLINEOPS_RENDERER_PORT must be one canonical TCP port.');
  }
  const port = Number(value);
  if (port > 65535) {
    throw new Error('OPENLINEOPS_RENDERER_PORT is outside the TCP port range.');
  }
  return port;
}
