"""Audit every controller action for an authorization guard.

Run from the Controllers directory:

    cd "Team Board/RafeeqyNotes.Api/Controllers"
    python ../tools/audit_guards.py

WHY THIS EXISTS
---------------
There are no automated tests in this project. This script is the only thing that will notice
a new controller action shipping without an organization guard, and it has already earned its
keep once: it caught a synchronous `GetPresence` action that a hand pass had missed.

An action counts as guarded if its body contains any of the accepted tokens below - an
explicit org guard, a platform-admin check, a membership filter on a list, or an identity
taken from the JWT. Endpoints that are legitimately public (auth, OAuth callback, public note
shares, the plan catalogue) show as gaps and should be eyeballed rather than "fixed".

Baseline at handoff: 142 of 157 actions guarded. If that number falls, something regressed.
"""

import io, os, re, sys

# Controllers whose actions are anonymous BY DESIGN, so an org guard is not the right check.
#
#   AuthController / GitHubAuthController - sign-in and OAuth callbacks, which by definition run
#     before there is a session to authorize.
#   CatalogController - the public marketing catalogue: the feature list, plan ladder and add-on
#     prices any visitor sees on the pricing page. It touches no organization, user or usage data.
#   PaymentsController - the payment provider's webhook and return URL. A gateway cannot present
#     a bearer token, so an org guard is not the available control; the guard is the HMAC
#     signature verified before any field is read. Do NOT add other actions to this file - an
#     unguarded endpoint would inherit the exemption silently.
#
# Adding a file here is a deliberate act. Anything not listed must guard every action.
EXEMPT_FILES = {"AuthController.cs", "GitHubAuthController.cs", "CatalogController.cs",
                "PaymentsController.cs"}

def audit(path):
    s = io.open(path, encoding="utf-8").read()
    parts = re.split(r'(\[Http[A-Za-z]+(?:\([^\)]*\))?\])', s)
    rows = []
    for i in range(1, len(parts), 2):
        body = parts[i + 1].split("[Http")[0] if i + 1 < len(parts) else ""
        sig = next((l.strip() for l in body.split("\n")
                    if l.strip().startswith("public")), "?")
        name = re.sub(r'.*?\s(\w+)\(.*', r'\1', sig)
        # An action counts as scoped if it either denies via a guard, or filters its
        # result set down to the caller's organizations.
        guarded = any(tok in body for tok in (
            "auth.Allowed",          # explicit org guard
            "IsPlatformAdmin",       # allowlisted cross-tenant capability
            "VisibleToCaller",       # list filtered to caller's orgs
            "MyOrganizationIds",     # ditto, inline
            "myOrgIds",
            "DenyIfNotParticipantAsync",  # chat: private to participants
            "IsParticipant",
            "CallerId",                  # per-user endpoints scoped by the JWT
            "OrgAccess.UserId(User)",    # ditto, inline
            "currentUserId",             # inline ownership filter
        ))
        rows.append((guarded, name))
    return rows

total_g = total_a = 0
for f in sorted(os.listdir(".")):
    if not f.endswith("Controller.cs"):
        continue
    rows = audit(f)
    if not rows:
        continue
    g = sum(1 for ok, _ in rows if ok)
    total_g += g; total_a += len(rows)
    mark = "exempt" if f in EXEMPT_FILES else ("OK " if g == len(rows) else "GAP")
    print(f"  {mark}  {f:<34} {g}/{len(rows)}")
    if g < len(rows) and f not in EXEMPT_FILES:
        for ok, name in rows:
            if not ok:
                print(f"           unguarded: {name}")
print(f"\n  total guarded actions: {total_g}/{total_a}")
