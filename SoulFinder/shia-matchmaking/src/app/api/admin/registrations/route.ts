import { NextRequest, NextResponse } from "next/server";
import { cookies } from "next/headers";
import { prisma } from "@/lib/prisma";

async function verifyAdmin(): Promise<boolean> {
  const cookieStore = await cookies();
  const session = cookieStore.get("admin_session");
  if (!session?.value) return false;
  try {
    const admin = await prisma.adminUser.findFirst({
      where: { id: session.value },
    });
    return !!admin;
  } catch {
    return false;
  }
}

export async function GET(request: NextRequest) {
  if (!(await verifyAdmin())) {
    return NextResponse.json({ error: "Unauthorized" }, { status: 401 });
  }

  const searchParams = request.nextUrl.searchParams;
  const page = Math.max(1, parseInt(searchParams.get("page") || "1") || 1);
  const limit = Math.min(100, Math.max(1, parseInt(searchParams.get("limit") || "20") || 20));
  const search = (searchParams.get("search") || "").slice(0, 200);
  const gender = searchParams.get("gender") || "";
  const status = searchParams.get("status") || "";
  const city = (searchParams.get("city") || "").slice(0, 100);

  const where: Record<string, unknown> = {};

  if (search) {
    where.OR = [
      { firstName: { contains: search } },
      { lastName: { contains: search } },
      { email: { contains: search } },
      { phone: { contains: search } },
    ];
  }

  if (gender) where.gender = gender;
  if (status) where.status = status;
  if (city) where.city = { contains: city };

  try {
    const [registrations, total] = await Promise.all([
      prisma.registration.findMany({
        where,
        include: { profilePhoto: true, payments: true },
        orderBy: { createdAt: "desc" },
        skip: (page - 1) * limit,
        take: limit,
      }),
      prisma.registration.count({ where }),
    ]);

    return NextResponse.json({
      registrations,
      total,
      page,
      totalPages: Math.ceil(total / limit),
    });
  } catch (error) {
    console.error("Error fetching registrations:", error);
    return NextResponse.json(
      { error: "Internal server error" },
      { status: 500 }
    );
  }
}
