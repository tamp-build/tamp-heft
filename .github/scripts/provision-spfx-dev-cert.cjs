// Pre-create the Heft/SPFx development certificate without trusting it, and print the path of its CA.
//
// On a headless Windows runner `heft start` hangs: debug-certificate-manager finds no trusted dev certificate
// and runs `certutil -user -addstore root`, which waits for a confirmation dialog nobody can click. Creating
// the certificate here with skipCertificateTrust, then adding its CA to the machine's root store (admin, no
// prompt) gives `heft start` a certificate it already considers trusted.
//
// Usage: node provision-spfx-dev-cert.cjs <spfx project dir>
'use strict';
const path = require('path');

const project = path.resolve(process.argv[2] || '.');
const load = (name) => require(require.resolve(name, { paths: [project] }));
const { CertificateManager } = load('@rushstack/debug-certificate-manager');
const { Terminal, ConsoleTerminalProvider } = load('@rushstack/terminal');

(async () => {
  const terminal = new Terminal(new ConsoleTerminalProvider({ verboseEnabled: true }));
  const manager = new CertificateManager();
  await manager.ensureCertificateAsync(true, terminal, { skipCertificateTrust: true });
  const caPath = manager.certificateStore.caCertificatePath;
  if (!caPath || !require('fs').existsSync(caPath)) throw new Error(`No CA certificate was written (expected ${caPath}).`);
  console.log(caPath);
})().catch((err) => {
  console.error(err && err.stack ? err.stack : err);
  process.exit(1);
});
