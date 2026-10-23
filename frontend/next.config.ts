import type { NextConfig } from "next";

const isDev = process.env.NODE_ENV === "development";

const nextConfig: NextConfig = {
  // Static export: served by the ASP.NET host from wwwroot, single image / single port.
  output: isDev ? undefined : "export",
  trailingSlash: true,
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
  // In dev the API is the running container; rewrites are unsupported with output: "export".
  ...(isDev && {
    rewrites: async () => [
      { source: "/api/:path*", destination: "http://localhost:8070/api/:path*" },
      { source: "/go/:path*", destination: "http://localhost:8070/go/:path*" },
    ],
  }),
};

export default nextConfig;
