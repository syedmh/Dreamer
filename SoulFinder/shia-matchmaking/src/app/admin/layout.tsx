import Link from "next/link";

export default function AdminLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <div className="flex min-h-screen flex-col md:flex-row">
      {/* Mobile Nav */}
      <div className="flex md:hidden items-center justify-between border-b border-border bg-[#0F0F1A] px-4 py-3">
        <h2 className="font-heading text-lg font-bold text-gold">Admin</h2>
        <nav className="flex items-center gap-4">
          <Link href="/admin" className="text-xs text-muted-foreground hover:text-gold">Dashboard</Link>
          <Link href="/admin/registrations" className="text-xs text-muted-foreground hover:text-gold">Registrations</Link>
          <a href="/api/admin/export?format=csv" className="text-xs text-muted-foreground hover:text-gold">Export</a>
          <Link href="/" className="text-xs text-gold">← Site</Link>
        </nav>
      </div>

      {/* Desktop Sidebar */}
      <aside className="hidden md:flex w-64 flex-col border-r border-border bg-[#0F0F1A]">
        <div className="p-6">
          <h2 className="font-heading text-lg font-bold text-gold">
            Admin Panel
          </h2>
          <p className="text-xs text-muted-foreground">Husaynia Matchmaking</p>
        </div>
        <nav className="flex-1 px-4 space-y-1">
          <Link
            href="/admin"
            className="flex items-center gap-3 rounded-lg px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-muted hover:text-white"
          >
            📊 Dashboard
          </Link>
          <Link
            href="/admin/registrations"
            className="flex items-center gap-3 rounded-lg px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-muted hover:text-white"
          >
            👥 Registrations
          </Link>
          <a
            href="/api/admin/export?format=csv"
            className="flex items-center gap-3 rounded-lg px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-muted hover:text-white"
          >
            📥 Export CSV
          </a>
        </nav>
        <div className="p-4 border-t border-border">
          <Link
            href="/"
            className="text-xs text-muted-foreground hover:text-gold transition-colors"
          >
            ← Back to Site
          </Link>
        </div>
      </aside>

      {/* Main Content */}
      <div className="flex-1 overflow-auto">
        <div className="p-4 sm:p-6 lg:p-8">{children}</div>
      </div>
    </div>
  );
}
