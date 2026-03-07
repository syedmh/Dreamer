import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import Link from "next/link";
import { ArrowLeft } from "lucide-react";

export default async function RegistrationDetailPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;

  // Placeholder - will fetch from DB
  return (
    <div>
      <Link
        href="/admin/registrations"
        className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-gold mb-6"
      >
        <ArrowLeft size={16} />
        Back to Registrations
      </Link>

      <div className="flex items-center justify-between mb-8">
        <h1 className="font-heading text-3xl font-bold text-white">
          Registration Detail
        </h1>
        <Badge className="bg-gray-600 text-white">Loading...</Badge>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <Card className="bg-card border-border">
          <CardHeader>
            <CardTitle className="text-gold">Personal Information</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-muted-foreground text-sm">
              Registration ID: {id}
            </p>
            <p className="text-muted-foreground text-sm mt-2">
              Connect your database to view full registration details.
            </p>
          </CardContent>
        </Card>

        <Card className="bg-card border-border">
          <CardHeader>
            <CardTitle className="text-gold">Religious Background</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-muted-foreground text-sm">
              Data will appear here once connected.
            </p>
          </CardContent>
        </Card>

        <Card className="bg-card border-border">
          <CardHeader>
            <CardTitle className="text-gold">Education & Career</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-muted-foreground text-sm">
              Data will appear here once connected.
            </p>
          </CardContent>
        </Card>

        <Card className="bg-card border-border">
          <CardHeader>
            <CardTitle className="text-gold">Partner Preferences</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-muted-foreground text-sm">
              Data will appear here once connected.
            </p>
          </CardContent>
        </Card>
      </div>

      {/* Match Notes */}
      <Card className="bg-card border-border mt-6">
        <CardHeader>
          <CardTitle className="text-gold">Matchmaking Notes</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-muted-foreground text-sm mb-4">
            Add notes about this registrant for matchmaking purposes.
          </p>
          <textarea
            className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground"
            placeholder="Add a note..."
            rows={3}
          />
          <button className="mt-2 rounded-md bg-primary-red px-4 py-2 text-sm text-white hover:bg-primary-red-light">
            Add Note
          </button>
        </CardContent>
      </Card>
    </div>
  );
}
