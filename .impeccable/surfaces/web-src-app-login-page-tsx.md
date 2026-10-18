---
version: 1
slug: "web-src-app-login-page-tsx"
primary_target: "web/src/app/login/page.tsx"
related_targets: ["web/src/app/register/page.tsx"]
---

# Surface brief: Habita app shell and auth (web/src/app)

Scope: Operate. Auth screens, org selector, invitation acceptance, app shell and Members table. Audience: owners and property-admin teams in Mexico, daily desktop use, some mobile. Job: collect rent clearly, trust the numbers. User brief: "profesional y limpio" (binding). Light only: daytime office desk. Language: es-MX. Unresolved: logo (a simple authored mark is placeholder), future modules' nav entries.

## Direction contract

THESIS: Habita is a receipt book kept in order: every screen is a clean leaf with a folio, ruled lines and tabular numerals, so money reads like a ledger and never like a dashboard toy. Refuses the category default of a purple-gradient SaaS shell with rounded icon cards.

OWN-WORLD: Cool paper ground (#F2F4F3) with white leaves, ink blue-black text (#13202B), ledger rules in pale green-grey (#D5DFDB), one accent: carbon-copy ink blue (#1D3FB8) for primary actions, selection and focus only. Semantic: stamp red for reversal/error, ledger green for paid/active, amber for pending. Geist Sans for everything, Geist Mono only for folios and ids, tabular numerals. Hairline rules instead of shadows; perforation (dotted rule with half-circle notches) separates a leaf's header from its body. Recognizable with content removed: white leaf, dotted perforation, blue ink.

STORY: The visitor sees a calm, exact tool that treats money with respect, signs in without friction, and lands in a workspace where roles, states and who-did-what are visible. They act: sign in, switch organization, invite and manage members.

FIRST VIEWPORT: Auth: centered 400px leaf on the paper ground, wordmark with the door-arch mark above, title, fields, full-width blue primary button, perforation between header and form, secondary links on one row below. App: 248px left rail on a slightly deeper paper (org name with folio-style id at top, nav, user block at bottom), content column max 960px with page title, one-line description and the primary action at the right of the title row.

FORM: Operate surface in the receipt-book (talonario) world; position 3 of 7 grounded candidates (statement, ledger, receipt book, plan sheet, letterhead, unit plaque, key tag). Seed key: 56006a5d.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
