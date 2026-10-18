import type { NextConfig } from "next";

const isProd = process.env.NODE_ENV === "production";

// Content-Security-Policy. Next.js injects small inline scripts/styles, so 'unsafe-inline' is kept for now
// (a nonce-based policy is the next hardening step). Everything else is locked to this origin; no framing, no plugins.
const csp = [
  "default-src 'self'",
  `script-src 'self' 'unsafe-inline'${isProd ? "" : " 'unsafe-eval'"}`,
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: blob:",
  "font-src 'self' data:",
  `connect-src 'self'${isProd ? "" : " ws: wss:"}`,
  "frame-ancestors 'none'",
  "base-uri 'self'",
  "form-action 'self'",
  "object-src 'none'",
  "manifest-src 'self'",
  "worker-src 'self'",
].join("; ");

const securityHeaders = [
  { key: "Content-Security-Policy", value: csp },
  { key: "X-Frame-Options", value: "DENY" },
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  { key: "Permissions-Policy", value: "camera=(self), microphone=(), geolocation=(), payment=(), usb=()" },
  { key: "Cross-Origin-Opener-Policy", value: "same-origin" },
  ...(isProd ? [{ key: "Strict-Transport-Security", value: "max-age=63072000; includeSubDomains" }] : []),
];

const nextConfig: NextConfig = {
  output: "standalone", // self-contained server for the production container
  poweredByHeader: false,
  // Next would gzip the live notification stream and hold it back until a whole block fills, so events never arrive. The edge proxy (Caddy)
  // does the compression in production, and it leaves the event stream alone.
  compress: false,
  // The stream passes through this proxy (the /api rewrite). Its default limit is 30 s; the API closes a stream after 5 minutes on its own.
  experimental: { proxyTimeout: 6 * 60 * 1000 },
  async headers() {
    return [
      { source: "/:path*", headers: securityHeaders },
      // The worker script must always be revalidated so an update reaches phones quickly.
      { source: "/sw.js", headers: [{ key: "Cache-Control", value: "no-cache, no-store, must-revalidate" }, { key: "Content-Type", value: "text/javascript; charset=utf-8" }] },
    ];
  },
  async rewrites() {
    return [{ source: "/api/:path*", destination: `${process.env.API_URL ?? "http://localhost:5222"}/api/:path*` }];
  },
};

export default nextConfig;
