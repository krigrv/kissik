# KISSIK compliance: accessibility, privacy and security

Self-assessment, 2026-11-02. This is the team's own review against the published standards, not a third-party
certification. Items marked **Open** must be closed before claiming conformance publicly.

## 1. Accessibility

| Standard | Where it applies | Target |
|---|---|---|
| **WCAG 2.2** (W3C) | Worldwide reference | Level AA |
| **Section 508** (US federal) and **ADA** | US public sector, US businesses | Section 508 points to WCAG 2.0 AA; we meet 2.2 AA, which covers it |
| **EN 301 549 v3.2.1** (EU) | EU public procurement; presumption of conformity for the EAA | Clause 11 (software) and clause 7 (video and captions) |
| **European Accessibility Act** (Directive 2019/882, in force 28 June 2025) | Products and services sold to EU consumers | Meet EN 301 549; publish an accessibility statement |

What KISSIK does (details and evidence: `docs/V1-QUALITY-AUDIT.md` Part 3):

| Requirement | How it is met | Status |
|---|---|---|
| Name, role, value (WCAG 4.1.2; EN 11.4.1.2) | Every control has an automation name; state changes are announced | Done |
| Status messages (WCAG 4.1.3) | Every status line is a UIA notification and a polite live region | Done (accessibility branch) |
| Keyboard (WCAG 2.1.1, 2.1.2) | Standard WinUI controls; shortcuts for record, pause, snapshot, hide | Done; scene-layer keyboard nudge **Open** |
| Dragging movements (WCAG 2.5.7) | Layers can be placed with number fields; arrow-key nudge being added | **Open** |
| Target size (WCAG 2.5.8) | All targets 40×40 px or more; Large controls mode 48×48 | Done (accessibility branch) |
| Focus visible and appearance (WCAG 2.4.7, 2.4.11, 2.4.13) | System focus rectangle kept everywhere | Done; contrast measurement **Open** |
| Contrast (WCAG 1.4.3, 1.4.11) | Windows theme brushes in light, dark and high contrast | A few fixed colours to switch under high contrast: **Open** |
| Resize text, reflow (WCAG 1.4.4, 1.4.10; EN 11.7) | Follows Windows text size; no fixed-height text containers | Live 200% check **Open** |
| User preferences (EN 11.7) | Follows Windows theme, high contrast, text size, animation settings | Done |
| Captions (EN 7.1; WCAG 1.2.4) | Live captions on device, burnt into the meeting camera and recordings | Done |
| No flashing (WCAG 2.3.1) | Nothing flashes; countdown changes once a second | Done |
| Colour not the only cue (WCAG 1.4.1) | States have words and icons; colour-blind preview built in | Done |
| Accessibility statement (EAA) | Publish on the website with a contact for problems | **Open** |
| Conformance report (VPAT / ACR) | Section 508 and EN 301 549 editions of the VPAT | **Open**, after the live checks |

## 2. Privacy

| Law | KISSIK position |
|---|---|
| **GDPR** and **ePrivacy** (EU/UK) | No account, no analytics, no tracking. Video, audio and captions stay on the PC. Crash reports are opt-in and show exactly what is sent. Sponsored picks are chosen on the PC from a public list; no personal data or identifiers are sent; can be turned off. |
| **CCPA / CPRA** (California) | No sale or sharing of personal information; nothing is collected to sell. |
| **DPDP Act 2023** (India) | No personal data processed by KISSIK's servers; opt-in crash reports are the only upload, with consent asked each time. |
| **Children (COPPA, GDPR art. 8)** | No accounts or data collection, so no age gating is needed. |

Data that leaves the PC, and only when the user turns it on:
- **Streaming:** video and audio go to the destinations the user enters.
- **Crash reports:** sent only after the user reviews and agrees.
- **Update check:** a plain request to GitHub for the latest version number.
- **Sponsored-picks list:** a plain download with no identifiers.

**Open:** publish a short privacy policy on the website saying the above.

## 3. Security

Reference standards: **OWASP ASVS 4.0 Level 1** for the local web endpoints, **Microsoft SDL**, and the
**CWE Top 25**. The detailed hardening is in commit e405ef9.

| Area | Control | Status |
|---|---|---|
| Phone pairing (ASVS V3) | 128-bit random key in the QR link, compared in constant time | Done |
| Brute force (ASVS V2.2) | Devices guessing wrong links 10+ times a minute are locked out | Done |
| Transport (ASVS V9) | TLS for Wi-Fi phones (self-made certificate pinned by the native app); plain HTTP only on localhost for USB | Done |
| Secrets at rest (ASVS V6) | Stream keys and the certificate's private key encrypted with DPAPI for the Windows user | Done |
| Local control API | Off by default; bound to 127.0.0.1; 192-bit bearer token; Host header checked (DNS rebinding); no CORS | Done |
| Denial of service | Header and body size caps, frame size cap, at most 16 connections | Done |
| Injection (CWE-78/88) | Stream destinations validated; FFmpeg arguments never take raw quotes or control characters | Done |
| Browser hardening | CSP, frame-ancestors none, no-referrer, nosniff, permissions policy on the phone page | Done |
| Outbound links | Only https links open from sponsored picks and updates | Done |
| Dependencies | `dotnet list package --vulnerable --include-transitive`: none; only permissive licences | Done; run before each release |
| Least privilege | Runs as the user (MSIX); asks Windows for camera/mic; never changes system settings | Done |
| Code signing (SDL) | MSIX must be signed by a trusted certificate or the Microsoft Store | **Open** |
| SBOM | Publish a CycloneDX bill of materials with each release | **Open** |
| Static analysis | Enable .NET security analyzers (CA2100, CA3xxx, CA5xxx) as errors in CI | **Open** |
| Vulnerability reporting | `SECURITY.md` with a private contact and response times | **Open** |

## 4. Before claiming conformance
1. Merge the accessibility branch and close its open items (colours, keyboard nudge, contrast measurement).
2. Run the live checks: Narrator, Accessibility Insights FastPass, 200% text, the high-contrast themes.
3. Publish the accessibility statement, privacy policy and `SECURITY.md`.
4. Sign the release, generate the SBOM, then fill in the VPAT (Section 508 and EN 301 549 editions).
