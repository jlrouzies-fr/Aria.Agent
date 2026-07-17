---
version: "0.2.0"
level: copilot
processes:
  design: copilot
  implementation: copilot
  documentation: copilot
  testing: copilot
  review: copilot
  deployment: copilot
components:
  src/AriaAgent/Aria.Agent: copilot
  src/AriaAgent/Aria.Bridge: copilot
  src/AriaAgent/Aria.Console: assisted
  src/AriaAgent/Aria.Harness: copilot
  src/AriaAgent/Aria.Shared: copilot
  src/AriaAgent/Aria.Tests: copilot
  src/AriaAgent/Aria.Tools: copilot
  src/AriaAgent/Aria.Web: copilot
  src/AriaAgent/Aria.Web.Client: copilot
  hindsight-custom/dockerfile: copilot
---

## Notes

- Development is now in **copilot mode** across the Web stack. As work shifted from the original `Aria.Console` terminal towards the Blazor web UI (`Aria.Web`), **Kimi Code** took over design, implementation, documentation, testing, review, and deployment assistance for `Aria.Web`, `Aria.Bridge`, `Aria.Harness`, and the surrounding components.
- `Aria.Console` remains **assisted**: it was originally authored by the human maintainer and is not driven in copilot mode.
- Architectural decisions, feature choices, and overall direction remain a collaborative conversation between the human author and Kimi Code.
