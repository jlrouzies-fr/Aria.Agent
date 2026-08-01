# AGENTS.md

Guidance for AI coding agents working in this repository. Read the security section before editing
anything under `Aria.Bridge/Services/Trust/`, `Aria.Bridge/Infrastructure/`, or `Aria.Shared/`.

## The one architectural fact that drives everything

There are two halves, and they are **not** equally trusted:

- **`Aria.Bridge`** is a daemon on the user's own machine (`localhost:5741`). It holds the soul's
  private key, chat history, OAuth tokens and LLM API keys. It is **authoritative**.
- **`Aria.Web`** is the hosted server. It is a switchboard that relays bytes. The threat model treats
  it as **fully compromised** (attacker A1 in `docs/readme/security.md`).

Almost every security control in this codebase exists to enforce that asymmetry. If a change makes
the bridge believe something merely because the server said it, that change is a bug regardless of
how reasonable it looks locally.

## Build and test

```bash
dotnet build src/AriaAgent/Aria.Bridge/Aria.Bridge.csproj
dotnet build src/AriaAgent/Aria.Web/Aria.Web.csproj
dotnet test  src/AriaAgent/Aria.Tests/Aria.Tests.csproj      # ~15s, run it before you finish
```

The full suite is fast. There is no excuse for not running it. CI runs it on every PR
(`.github/workflows/pr-build.yml`), overriding the `osx-arm64` RID that `Aria.Tests.csproj` and
`Aria.Console.csproj` pin for local development.

## Security invariants — do not break these

Each is enforced somewhere in code and asserted somewhere in tests. If your change requires bending
one, stop and raise it with a human instead of working around it.

1. **Grant-accepting keys come only from locally-held or human-confirmed material.**
   `ContextGrantStore.AcceptableKeysAsync` may include the soul key (primary holds the private half),
   this node's own node key, and siblings verified against those. Never a key the server supplied.
   A joined node has no soul key until a human pins one as the last join step (Soul panel →
   **JOIN · CONFIRM MASTER KEY**, or `SoulPinEndpoints`) — see
   `SiblingRoster.ResolveSoulMasterPublicKey`.
2. **New bridge endpoints are local-only by default.** `Aria.Shared.TunnelAllowlist` is an explicit
   opt-in list of what the server may reach. Adding a path there is a security decision, not
   plumbing. Anything that mints trust, writes keys, or performs a human ceremony stays off it.
3. **Mutating requests must be local-origin.** `LocalOriginMiddleware` guards
   POST/PUT/DELETE/PATCH. Its bypass list is tiny and each entry has a stated reason.
4. **The node decides policy, the server may only narrow it.** File, git and terminal scope resolve
   through `NodeTerminalPolicy` / `SecurityPolicy.FromNodeAndRequest`. An empty node-side allowed-path
   list blocks everything; it never means "allow all".
5. **Seals sign exactly the text the human saw**, bound to one capability (`SealStatement.Build`).
6. **Fail closed.** When trust cannot be established, refuse and log loudly. Never degrade to
   permissive.

## Two mistakes that have actually happened here

Both shipped, passed review, and passed a green test suite. Pattern-match against them.

**Deleting a fail-closed guard to make a feature work.** A joined node's roster refresh began with an
early return when no soul key was present. It blocked a legitimate feature (approvals replicating to a
second machine), so it was deleted. That silently widened the set of keys the node would trust.
If a guard blocks your feature, *extend the trust path properly* — do not remove the guard.

**Treating a signature chain as a trust anchor.** Verifying a certificate proves nothing if the key
you verify against arrived through the same untrusted channel as the certificate. A malicious relay
can supply a self-consistent set. Always ask: *where did the root of this chain come from, and could
the attacker have chosen it?* If the answer is the server, it is not an anchor.

## When you touch trust-critical code

- Say plainly, in the PR or your summary, what a compromised `Aria.Web` can and cannot do after your
  change. If you cannot articulate it, you do not understand the change yet.
- Write the adversarial test, not the convenient one. A prior test named
  `JoinedNode_DoesNotTrustUnverifiedPrimaryKey` passed for the entire lifetime of the vulnerability it
  was named after, because it modelled a weaker attacker than the real one.
- Prefer making the unsafe state unrepresentable (types, required ceremonies) over documenting that it
  is unsafe.

## Conventions

- Comments explain intent, constraints and trade-offs — never what the next line does, and never that
  a change is correct. Match the density of the surrounding file.
- Security-relevant code carries a short header comment naming the attack it stops. Keep that current;
  it is the only documentation most readers (human or model) will ever see.
- Bridge status page UI lives in `Aria.Bridge/Frontend/BridgeStatusPage/*.cs` as C# raw string
  literals, not separate asset files.
- **Aria.Web tooltips use `[data-tip]`, never bare `title=`.** The shared cursor-following tip is
  `#aria-tip` in `wwwroot/css/theme/tooltip.css` + the handler at the top of
  `wwwroot/aria-interop.js`. Native browser tooltips look wrong and are easy to ship by accident.
  Opt into a variant with `data-tip-variant` when the state needs chrome (`loading`, `action`,
  `warn`). New hover copy on icons, badges, and status dots must use this system.

## Reference

- `docs/readme/security.md` — user-facing model, attacker definitions, controls F-1 … F-14
- `docs/security/defense-in-depth-plan.md` — layer design
- `docs/security/hardening-plan.md` — F-numbered control rationale
- `docs/security/phase2-context-grants-remaining.md` — Layer B grants, co-equal approval, node pinning
