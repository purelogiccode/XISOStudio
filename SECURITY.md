# Security Policy

XISO Studio processes untrusted disc images and archives, so security reports are taken seriously.
Thank you for helping keep the application and its users safe.

## Supported Versions

| Version              | Supported |
|:---------------------|:----------|
| Latest release       | ✅        |
| Older releases       | ❌        |

Only the latest published release is supported. Please reproduce the issue on the latest version
before reporting it; older releases do not receive security fixes.

## Reporting a Vulnerability

**Do not open a public GitHub issue for a security vulnerability.**

Use GitHub's private vulnerability reporting:

1. Open <https://github.com/purelogiccode/XISOStudio/security>.
2. Click **Report a vulnerability**.
3. Describe the issue privately to the maintainers.

If private reporting is unavailable, use the contact form at
<https://www.purelogiccode.com/#contact> and ask for a private channel. Do not include exploit
details in a public issue or in the first contact message.

Please include as much of the following as possible:

- The affected XISO Studio version (shown in the **About** window) and platform/architecture.
- A description of the vulnerability and its potential impact.
- Steps to reproduce, ideally with a minimal proof of concept.
- Relevant log lines from `%LocalAppData%\XISOStudio\logs` (or the equivalent location on
  Linux/macOS), with personal paths and other sensitive data removed.
- Any suggested fix or mitigation.

## What to Expect

- We aim to acknowledge reports within five business days.
- We will investigate, confirm the impact, and keep you informed of the assessment and fix.
- Confirmed issues are fixed in the latest release. We will coordinate disclosure with you and are
  happy to credit you unless you prefer to remain anonymous.
- Please give us a reasonable opportunity to release a fix before disclosing the issue publicly.
- This project does not offer a paid bug bounty.

## Scope

In scope:

- Vulnerabilities in the application code in this repository.
- Unsafe handling of malicious images, archives, or file paths (for example path traversal,
  arbitrary file overwrite, or code execution).
- Vulnerabilities in the update checker, bug-report pipeline, or other network-facing code.
- Security problems caused by the way third-party dependencies or bundled tools are used or
  packaged by XISO Studio.

Out of scope:

- Vulnerabilities in third-party libraries or the bundled 7-Zip binaries that have no demonstrated
  impact in XISO Studio; please report those upstream.
- Issues that require physical access to the machine, an already-compromised host, or social
  engineering.
- Automated scanner output without a demonstrated, exploitable impact.
- Legal or copyright questions about game images; those are not security issues.

## Safe Harbor

We will not pursue legal action for good-faith security research that follows this policy. To stay
within safe harbor, do not access other users' data, do not disrupt services, avoid destroying or
modifying data you do not own, and give us reasonable time to fix the issue before public
disclosure.
