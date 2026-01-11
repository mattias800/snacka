# Code Signing Guide

This document explains how to set up code signing for Snacka releases. Code signing is essential for:
- Preventing "unidentified developer" warnings
- Building user trust
- Required for distribution through app stores

## Overview

| Platform | Certificate Type | Cost | Distribution |
|----------|-----------------|------|--------------|
| macOS | Apple Developer ID | $99/year | Direct download, DMG |
| Windows | Code Signing Certificate | $200-500/year | Direct download, installer |
| Linux | GPG Signing | Free | AppImage, packages |

---

## macOS Code Signing & Notarization

### Prerequisites

1. **Apple Developer Program membership** ($99/year)
   - Sign up at https://developer.apple.com/programs/

2. **Developer ID Application certificate**
   - Created in Apple Developer portal → Certificates

3. **App-specific password** for notarization
   - Created at https://appleid.apple.com/account/manage

### Step 1: Create Developer ID Certificate

1. Go to https://developer.apple.com/account/resources/certificates/list
2. Click "+" to create a new certificate
3. Select "Developer ID Application"
4. Follow the instructions to create a Certificate Signing Request (CSR)
5. Download and install the certificate

### Step 2: Export Certificate for CI

```bash
# Export certificate to .p12 file
security export -k login.keychain -t identities -f pkcs12 -o certificate.p12 -P "password"

# Convert to base64 for GitHub Secrets
base64 -i certificate.p12 | pbcopy
```

### Step 3: Configure GitHub Secrets

Add these secrets to your repository (Settings → Secrets and variables → Actions):

| Secret | Description |
|--------|-------------|
| `MACOS_CERTIFICATE` | Base64-encoded .p12 certificate |
| `MACOS_CERTIFICATE_PWD` | Password for the .p12 file |
| `APPLE_ID` | Your Apple ID email |
| `APPLE_APP_PASSWORD` | App-specific password |
| `APPLE_TEAM_ID` | Your 10-character Team ID |

### Step 4: Update Workflow

Add this step to the macOS build job in `.github/workflows/build-client.yml`:

```yaml
- name: Import Code Signing Certificate
  if: env.MACOS_CERTIFICATE != ''
  env:
    MACOS_CERTIFICATE: ${{ secrets.MACOS_CERTIFICATE }}
    MACOS_CERTIFICATE_PWD: ${{ secrets.MACOS_CERTIFICATE_PWD }}
  run: |
    # Create keychain
    security create-keychain -p "" build.keychain
    security default-keychain -s build.keychain
    security unlock-keychain -p "" build.keychain

    # Import certificate
    echo $MACOS_CERTIFICATE | base64 --decode > certificate.p12
    security import certificate.p12 -k build.keychain -P "$MACOS_CERTIFICATE_PWD" -T /usr/bin/codesign
    security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "" build.keychain
    rm certificate.p12

- name: Create .app bundle (signed)
  env:
    CODESIGN_IDENTITY: "Developer ID Application: Your Name (TEAMID)"
  run: |
    ./installers/macos/create-app-bundle.sh ./publish ./output ${{ needs.version.outputs.version }} arm64

- name: Create DMG (signed & notarized)
  env:
    APPLE_ID: ${{ secrets.APPLE_ID }}
    APPLE_APP_PASSWORD: ${{ secrets.APPLE_APP_PASSWORD }}
    APPLE_TEAM_ID: ${{ secrets.APPLE_TEAM_ID }}
  run: |
    ./installers/macos/create-dmg.sh ./output/Snacka.app ./output/Snacka-$VERSION-macOS-arm64.dmg "Snacka"
```

### Local Signing (Development)

```bash
# Find your signing identity
security find-identity -v -p codesigning

# Sign the app
export CODESIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
./installers/macos/create-app-bundle.sh ./publish ./output 0.1.0 arm64

# Verify signature
codesign -v --deep --strict ./output/Snacka.app
spctl -a -t exec -vv ./output/Snacka.app
```

---

## Windows Code Signing

### Certificate Options

1. **Extended Validation (EV) Certificate** - ~$400-500/year
   - Immediate SmartScreen reputation
   - Hardware token required
   - Recommended for production

2. **Standard Code Signing Certificate** - ~$200-300/year
   - Requires reputation building with SmartScreen
   - Software-based

### Certificate Providers

- DigiCert (recommended)
- Sectigo
- GlobalSign

### Step 1: Purchase Certificate

1. Purchase from a certificate provider
2. Complete validation process
3. Receive certificate (EV: hardware token, Standard: .pfx file)

### Step 2: Configure GitHub Secrets

| Secret | Description |
|--------|-------------|
| `WINDOWS_CERTIFICATE` | Base64-encoded .pfx certificate |
| `WINDOWS_CERTIFICATE_PWD` | Password for the .pfx file |

### Step 3: Update Workflow

```yaml
- name: Import Code Signing Certificate
  if: env.WINDOWS_CERTIFICATE != ''
  env:
    WINDOWS_CERTIFICATE: ${{ secrets.WINDOWS_CERTIFICATE }}
    WINDOWS_CERTIFICATE_PWD: ${{ secrets.WINDOWS_CERTIFICATE_PWD }}
  run: |
    $certBytes = [Convert]::FromBase64String($env:WINDOWS_CERTIFICATE)
    [IO.File]::WriteAllBytes("certificate.pfx", $certBytes)

- name: Create signed installer
  run: |
    & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
      installers/windows/Snacka.iss `
      /DVersion=${{ needs.version.outputs.version }} `
      /DSourceDir=${{ github.workspace }}\publish `
      /DOutputDir=${{ github.workspace }}\output `
      "/DSignTool=signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /f certificate.pfx /p ${{ secrets.WINDOWS_CERTIFICATE_PWD }} `$f"
```

### Local Signing

```powershell
# Sign executable
signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /f certificate.pfx /p password Snacka.Client.exe

# Verify signature
signtool verify /pa Snacka.Client.exe
```

---

## Linux GPG Signing

Linux AppImages can be signed with GPG for verification.

### Step 1: Create GPG Key

```bash
# Generate key
gpg --full-generate-key
# Select: RSA and RSA, 4096 bits, no expiration

# Export public key
gpg --armor --export your@email.com > snacka-signing-key.asc

# Export private key (for CI)
gpg --armor --export-secret-keys your@email.com | base64
```

### Step 2: Configure GitHub Secrets

| Secret | Description |
|--------|-------------|
| `GPG_PRIVATE_KEY` | Base64-encoded private key |
| `GPG_PASSPHRASE` | Key passphrase |

### Step 3: Update Workflow

```yaml
- name: Import GPG Key
  if: env.GPG_PRIVATE_KEY != ''
  env:
    GPG_PRIVATE_KEY: ${{ secrets.GPG_PRIVATE_KEY }}
  run: |
    echo "$GPG_PRIVATE_KEY" | base64 -d | gpg --import

- name: Sign AppImage
  env:
    GPG_PASSPHRASE: ${{ secrets.GPG_PASSPHRASE }}
  run: |
    gpg --batch --yes --pinentry-mode loopback --passphrase "$GPG_PASSPHRASE" \
      --detach-sign --armor ./output/Snacka-*.AppImage
```

### Verification

Users can verify the AppImage:

```bash
# Import public key
gpg --import snacka-signing-key.asc

# Verify signature
gpg --verify Snacka-0.1.0-x86_64.AppImage.asc Snacka-0.1.0-x86_64.AppImage
```

---

## Cost Summary

| Item | Cost | Frequency |
|------|------|-----------|
| Apple Developer Program | $99 | Annual |
| Windows EV Certificate | ~$400-500 | Annual |
| Windows Standard Certificate | ~$200-300 | Annual |
| Linux GPG Key | Free | One-time |

**Minimum recommended**: Apple + Windows Standard = ~$300-400/year

---

## Troubleshooting

### macOS: "Developer cannot be verified"

The app isn't signed or notarized. Users can:
1. Right-click → Open → Open
2. Or: System Preferences → Security & Privacy → "Open Anyway"

### macOS: Notarization fails

Common issues:
- Hardened runtime not enabled
- Unsigned nested code (dylibs)
- Invalid entitlements

```bash
# Check notarization issues
xcrun notarytool log <submission-id> --apple-id ... --password ... --team-id ...
```

### Windows: SmartScreen warning

For standard (non-EV) certificates:
- SmartScreen builds reputation over time
- More downloads = fewer warnings
- EV certificates bypass this

### Windows: "Publisher: Unknown"

The executable isn't signed. Verify with:
```powershell
signtool verify /pa Snacka.Client.exe
```

---

## Resources

- [Apple Developer ID](https://developer.apple.com/developer-id/)
- [Apple Notarization](https://developer.apple.com/documentation/security/notarizing_macos_software_before_distribution)
- [Microsoft Authenticode](https://docs.microsoft.com/en-us/windows/win32/seccrypto/cryptography-tools)
- [AppImage Signing](https://docs.appimage.org/packaging-guide/optional/signatures.html)
