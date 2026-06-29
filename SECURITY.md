# Security Policy

## Reporting a vulnerability

If you discover a security vulnerability in KingshotAuto, please report it **privately** so it can be fixed before it is widely known.

Preferred method:

- **GitHub Security Advisories** — open a private advisory on the repository:
  <https://github.com/KingshotAuto/Kingshot-bot/security/advisories/new>

If you cannot use Security Advisories:

- Open a **minimal** issue at <https://github.com/KingshotAuto/Kingshot-bot/issues> that says you have found a security problem and asks a maintainer to get in touch. **Do not include exploit details, proof-of-concept code, or sensitive information in the public issue.**

Please give maintainers a reasonable amount of time to respond and address the issue before any public disclosure. As this is a volunteer-run open-source project, response times may vary.

When reporting, it helps to include (privately):

- A description of the issue and its impact
- Steps to reproduce
- Affected version / commit
- Any suggested fix, if you have one

## Supported versions

This is a community project with limited resources. Only the **latest `main`** branch is supported. Please make sure you are on the latest `main` before reporting an issue.

| Version        | Supported |
| -------------- | --------- |
| latest `main`  | ✅        |
| anything older | ❌        |

## No telemetry, no remote code, no auto-update

KingshotAuto runs entirely on your machine. The original licensing/activation, analytics/telemetry, and auto-update systems have been **completely removed**. A build:

- contacts **no servers**,
- sends **no data anywhere**, and
- has **no remote code-execution or self-update path** — it will not download or run code from the internet.

This means there is no server-side component to attack and no update channel that could push malicious code to users. Security-relevant issues are therefore limited to the local application itself.

## Game Terms of Service / anti-cheat disclaimer

KingshotAuto automates a third-party game by controlling an Android emulator. **Using it may violate that game's Terms of Service and could trigger anti-cheat systems, resulting in account suspension or banning.** This is an inherent risk of game automation, not a software vulnerability, and reports about ToS/anti-cheat detection are out of scope for this security policy.

The software is provided **"as is", without warranty of any kind** (see the [MIT License](LICENSE)). **Use at your own risk.**
