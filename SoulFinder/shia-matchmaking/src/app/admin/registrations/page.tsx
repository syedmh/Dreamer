"use client";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import Link from "next/link";
import { Search } from "lucide-react";

export default function RegistrationsPage() {
  // Placeholder - will fetch from API
  const registrations: Array<{
    id: string;
    firstName: string;
    lastName: string;
    email: string;
    gender: string;
    city: string;
    state: string;
    status: string;
    createdAt: string;
  }> = [];

  const statusColor: Record<string, string> = {
    PAID: "bg-primary-red text-white",
    PENDING_PAYMENT: "bg-yellow-600 text-white",
    CONFIRMED: "bg-blue-600 text-white",
    CANCELLED: "bg-red-600 text-white",
    DRAFT: "bg-gray-600 text-white",
    EXPIRED: "bg-gray-500 text-white",
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-8">
        <h1 className="font-heading text-3xl font-bold text-white">
          Registrations
        </h1>
        <a
          href="/api/admin/export?format=csv"
          className="text-sm text-gold hover:underline"
        >
          📥 Export CSV
        </a>
      </div>

      {/* Search & Filters */}
      <Card className="bg-card border-border mb-6">
        <CardContent className="pt-6">
          <div className="flex flex-wrap gap-4">
            <div className="relative flex-1 min-w-[200px]">
              <Search className="absolute left-3 top-3 h-4 w-4 text-muted-foreground" />
              <Input
                placeholder="Search by name, email, phone..."
                className="pl-10 bg-background border-border"
              />
            </div>
            <select className="rounded-md border border-border bg-[#1A1A2E] px-3 py-2 text-sm text-white [&>option]:bg-[#1A1A2E] [&>option]:text-white">
              <option value="">All Genders</option>
              <option value="MALE">Male</option>
              <option value="FEMALE">Female</option>
            </select>
            <select className="rounded-md border border-border bg-[#1A1A2E] px-3 py-2 text-sm text-white [&>option]:bg-[#1A1A2E] [&>option]:text-white">
              <option value="">All Statuses</option>
              <option value="PAID">Paid</option>
              <option value="PENDING_PAYMENT">Pending Payment</option>
              <option value="CONFIRMED">Confirmed</option>
              <option value="CANCELLED">Cancelled</option>
            </select>
          </div>
        </CardContent>
      </Card>

      {/* Table */}
      <Card className="bg-card border-border">
        <CardHeader>
          <CardTitle className="text-white">
            All Registrations ({registrations.length})
          </CardTitle>
        </CardHeader>
        <CardContent>
          {registrations.length === 0 ? (
            <p className="text-center text-muted-foreground py-12">
              No registrations yet. They will appear here once people register.
            </p>
          ) : (
            <div className="overflow-x-auto -mx-4 sm:mx-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Email</TableHead>
                  <TableHead>Gender</TableHead>
                  <TableHead>Location</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Date</TableHead>
                  <TableHead></TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {registrations.map((reg) => (
                  <TableRow key={reg.id}>
                    <TableCell className="text-white font-medium">
                      {reg.firstName} {reg.lastName}
                    </TableCell>
                    <TableCell className="text-muted-foreground">
                      {reg.email}
                    </TableCell>
                    <TableCell>
                      <Badge variant="outline" className="border-border">
                        {reg.gender}
                      </Badge>
                    </TableCell>
                    <TableCell className="text-muted-foreground">
                      {reg.city}, {reg.state}
                    </TableCell>
                    <TableCell>
                      <Badge className={statusColor[reg.status] || "bg-gray-600"}>
                        {reg.status}
                      </Badge>
                    </TableCell>
                    <TableCell className="text-muted-foreground text-sm">
                      {new Date(reg.createdAt).toLocaleDateString()}
                    </TableCell>
                    <TableCell>
                      <Link
                        href={`/admin/registrations/${reg.id}`}
                        className="text-gold hover:underline text-sm"
                      >
                        View
                      </Link>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
