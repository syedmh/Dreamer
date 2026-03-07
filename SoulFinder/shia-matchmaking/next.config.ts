import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Output standalone for Azure App Service deployment
  output: process.env.NODE_ENV === "production" ? "standalone" : undefined,

  // Allow Azure Blob Storage images
  images: {
    remotePatterns: [
      {
        protocol: "https",
        hostname: "*.blob.core.windows.net",
      },
    ],
  },

  // Server external packages for Prisma
  serverExternalPackages: ["@prisma/client"],
};

export default nextConfig;
