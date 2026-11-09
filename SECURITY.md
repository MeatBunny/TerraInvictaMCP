# Security policy

## Reporting

- Do not open a public issue. Use GitHub's private vulnerability reporting on
  this repository (Security tab, "Report a vulnerability").
- Send a working example that you ran against real project code. We will
  not fix a report that has no example.
- Describe what you saw and the steps to make it happen. Leave out
  severity ratings, CVSS scores and CWE numbers. Do not paste scanner
  output as a finding.

## Known and accepted

We know and accept two behaviors, so do not report them. Both are
covered under "Safety and privacy" in `README.md`.

- **The bridge socket has no authentication.**
- **Console commands can unlock Steam achievements.**

## Supported versions

We only support the current release. Fixes go on top of `main`. There are
no backports.
