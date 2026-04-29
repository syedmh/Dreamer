import Link from "next/link";

const features = [
  {
    icon: "🔒",
    title: "Encrypted Vault",
    description:
      "Store text notes, voice memos, videos, and documents in a secure, encrypted digital vault.",
  },
  {
    icon: "🤝",
    title: "Trusted Delivery",
    description:
      "Designate recipients and trusted contacts to ensure your messages reach the right people.",
  },
  {
    icon: "📜",
    title: "Will Management",
    description:
      "Manage your digital will, assign executors, and add legal notes and special instructions.",
  },
];

export default function Home() {
  return (
    <div className="flex flex-1 flex-col">
      {/* Hero */}
      <section className="flex flex-1 flex-col items-center justify-center px-4 py-24 text-center">
        <h1 className="mb-4 max-w-3xl text-4xl font-extrabold tracking-tight text-white sm:text-5xl lg:text-6xl">
          Secure your legacy.{" "}
          <span className="text-indigo-400">Deliver your memories.</span>
        </h1>
        <p className="mb-10 max-w-xl text-lg text-slate-400">
          WillVault lets you store important messages, documents, and final
          wishes in a secure digital vault — delivered to the people who matter
          most, when the time comes.
        </p>
        <div className="flex flex-col gap-4 sm:flex-row">
          <Link
            href="/register"
            className="rounded-lg bg-indigo-600 px-8 py-3 text-base font-semibold text-white shadow-lg shadow-indigo-600/25 hover:bg-indigo-500 transition-colors"
          >
            Get Started
          </Link>
          <a
            href="#features"
            className="rounded-lg border border-slate-600 px-8 py-3 text-base font-semibold text-slate-300 hover:bg-slate-800 transition-colors"
          >
            Learn More
          </a>
        </div>
      </section>

      {/* Features */}
      <section
        id="features"
        className="border-t border-slate-800 bg-slate-900/50 px-4 py-20"
      >
        <div className="mx-auto max-w-5xl">
          <h2 className="mb-12 text-center text-3xl font-bold text-white">
            Everything you need to protect what matters
          </h2>
          <div className="grid gap-8 sm:grid-cols-3">
            {features.map((f) => (
              <div
                key={f.title}
                className="rounded-xl border border-slate-700/50 bg-slate-800/50 p-6 text-center"
              >
                <div className="mb-4 text-4xl">{f.icon}</div>
                <h3 className="mb-2 text-lg font-semibold text-white">
                  {f.title}
                </h3>
                <p className="text-sm text-slate-400">{f.description}</p>
              </div>
            ))}
          </div>
        </div>
      </section>
    </div>
  );
}
