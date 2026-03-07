import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { prisma } from "@/lib/prisma";
import { Users, DollarSign, UserCheck, Clock } from "lucide-react";

export const dynamic = "force-dynamic";

export default async function AdminDashboardPage() {
  let stats = { total: 0, paid: 0, male: 0, female: 0, revenue: 0, pending: 0 };

  try {
    const [total, paid, male, female, pending, payments] = await Promise.all([
      prisma.registration.count(),
      prisma.registration.count({ where: { status: "PAID" } }),
      prisma.registration.count({ where: { gender: "MALE" } }),
      prisma.registration.count({ where: { gender: "FEMALE" } }),
      prisma.registration.count({ where: { status: "PENDING_PAYMENT" } }),
      prisma.paymentRecord.findMany({
        where: { status: "succeeded" },
        select: { amount: true },
      }),
    ]);
    const revenue = payments.reduce((sum: number, p: { amount: number }) => sum + p.amount, 0);
    stats = { total, paid, male, female, revenue, pending };
  } catch {
    // DB not available yet — show zeros
  }

  return (
    <div>
      <h1 className="font-heading text-3xl font-bold text-white mb-8">
        Dashboard
      </h1>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4 mb-8">
        <Card className="bg-card border-border">
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Total Registrations
            </CardTitle>
            <Users className="h-4 w-4 text-gold" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold text-white">{stats.total}</div>
          </CardContent>
        </Card>

        <Card className="bg-card border-border">
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Paid
            </CardTitle>
            <UserCheck className="h-4 w-4 text-primary-red-light" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold text-white">{stats.paid}</div>
          </CardContent>
        </Card>

        <Card className="bg-card border-border">
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Revenue
            </CardTitle>
            <DollarSign className="h-4 w-4 text-gold" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold text-white">
              ${(stats.revenue / 100).toFixed(2)}
            </div>
          </CardContent>
        </Card>

        <Card className="bg-card border-border">
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Gender Split
            </CardTitle>
            <Clock className="h-4 w-4 text-gold" />
          </CardHeader>
          <CardContent>
            <div className="text-sm text-white">
              <span className="text-blue-400">{stats.male} Male</span>
              {" / "}
              <span className="text-pink-400">{stats.female} Female</span>
            </div>
            {stats.pending > 0 && (
              <p className="text-xs text-muted-foreground mt-1">
                {stats.pending} pending payment
              </p>
            )}
          </CardContent>
        </Card>
      </div>

      <Card className="bg-card border-border">
        <CardContent className="pt-6">
          <p className="text-muted-foreground text-center py-8">
            {stats.total === 0
              ? "No registrations yet. Submit the registration form to see data here."
              : `${stats.total} registration(s) in the system. Visit Registrations for details.`}
          </p>
        </CardContent>
      </Card>
    </div>
  );
}
