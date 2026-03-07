import { NextRequest, NextResponse } from "next/server";
import { prisma } from "@/lib/prisma";

export async function GET(request: NextRequest) {
  // TODO: Add auth check
  const searchParams = request.nextUrl.searchParams;
  const format = searchParams.get("format") || "csv";

  try {
    const registrations = await prisma.registration.findMany({
      include: { payments: true },
      orderBy: { createdAt: "desc" },
    });

    if (format === "csv") {
      const headers = [
        "ID", "Status", "First Name", "Middle Name", "Last Name", "Email",
        "Phone", "Gender", "Date of Birth", "City", "State", "Country of Origin",
        "Languages", "Practicing Muslim", "Sect", "Prayer Frequency",
        "Education Level", "Occupation", "Income Range", "Marital Status",
        "Has Children", "Height", "Created At",
      ];

      const rows = registrations.map((r: { id: string; status: string; firstName: string; middleName: string | null; lastName: string; email: string; phone: string; gender: string; dateOfBirth: Date; city: string; state: string; countryOfOrigin: string | null; languages: string; practicingMuslim: string | null; sect: string | null; prayerFrequency: string | null; educationLevel: string | null; occupation: string | null; incomeRange: string | null; maritalStatus: string | null; hasChildren: boolean | null; height: string | null; createdAt: Date }) => [
        r.id, r.status, r.firstName, r.middleName || "", r.lastName, r.email,
        r.phone, r.gender, r.dateOfBirth.toISOString().split("T")[0],
        r.city, r.state, r.countryOfOrigin || "", r.languages,
        r.practicingMuslim || "", r.sect || "", r.prayerFrequency || "",
        r.educationLevel || "", r.occupation || "", r.incomeRange || "",
        r.maritalStatus || "", r.hasChildren ? "Yes" : "No", r.height || "",
        r.createdAt.toISOString(),
      ]);

      const csvContent = [
        headers.join(","),
        ...rows.map((row: string[]) =>
          row.map((cell: string) => `"${String(cell).replace(/"/g, '""')}"`).join(",")
        ),
      ].join("\n");

      return new NextResponse(csvContent, {
        headers: {
          "Content-Type": "text/csv",
          "Content-Disposition": `attachment; filename=registrations-${new Date().toISOString().split("T")[0]}.csv`,
        },
      });
    }

    return NextResponse.json(registrations);
  } catch (error) {
    console.error("Export error:", error);
    return NextResponse.json(
      { error: "Internal server error" },
      { status: 500 }
    );
  }
}
