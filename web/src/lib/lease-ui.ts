import type { LeaseStatus } from "@/lib/api";
import type { Tone } from "@/components/ui";

export const leaseTone: Record<LeaseStatus, Tone> = { Draft: "neutral", Active: "ledger", Ended: "neutral", Cancelled: "stamp" };
