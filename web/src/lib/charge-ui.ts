import type { ChargeStatus } from "@/lib/api";
import type { Tone } from "@/components/ui";

export const chargeTone: Record<ChargeStatus, Tone> = { Open: "neutral", PartiallyPaid: "pending", Paid: "ledger", Overdue: "stamp", Voided: "neutral" };
