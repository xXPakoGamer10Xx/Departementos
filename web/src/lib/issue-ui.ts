import type { IssuePriority, IssueStatus } from "@/lib/api";
import type { Tone } from "@/components/ui";

export const priorityTone: Record<IssuePriority, Tone> = { Low: "neutral", Normal: "neutral", High: "pending", Urgent: "stamp" };
export const issueStatusTone: Record<IssueStatus, Tone> = { Open: "neutral", InProgress: "pending", Waiting: "neutral", Resolved: "ledger", Closed: "neutral" };
