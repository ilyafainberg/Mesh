# Code signing (Azure Trusted Signing)

Mesh binaries are signed with Azure Trusted Signing so Windows SmartScreen does not
warn users. This is the setup and per-release process.

## One-time setup

Provisioned (done):
- Resource group `rg-mesh-signing` (North Europe)
- Trusted Signing account `mesh-signing` (Basic SKU, ~$9.99/mo)
- Endpoint: `https://neu.codesigning.azure.net/`

Remaining (needs a human + company legal docs):

1. **Identity validation (portal only).** Azure portal -> `mesh-signing` account ->
   Identity validations -> New -> **Organization**.
   Enter the organization's exact registered legal name, address, and business identifier
   (VAT / company number). Submit. Microsoft verifies against public business
   records; approval takes ~1-5 business days.
   (EU organizations are eligible; EU individuals are not, which is why this must be
   the registered company, not a personal identity.)

   Direct link:
   `https://portal.azure.com/#@efd78fe9-febc-406f-815a-16a98942bab3/resource/subscriptions/e263ca58-3bfa-4b52-98f0-1df2824a6995/resourceGroups/rg-mesh-signing/providers/Microsoft.CodeSigning/codeSigningAccounts/mesh-signing/identityValidations`

2. **Create a certificate profile** (after validation is `Completed`):
   ```
   az trustedsigning certificate-profile create \
     -g rg-mesh-signing --account-name mesh-signing \
     -n mesh-cert --profile-type PublicTrust \
     --identity-validation-id <validation-id>
   ```

3. **Signing service principal** (for CI / local signing):
   - Create an Entra app registration, add a client secret.
   - Assign it the role **"Trusted Signing Certificate Profile Signer"** scoped to the
     `mesh-signing` account (or the specific cert profile).
   - Set env vars where signing runs: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`,
     `AZURE_CLIENT_SECRET`.

## Per-release signing

Sign AFTER publishing/compiling but BEFORE zipping, so the signatures ship:

```
_deploy\sign-release.ps1 -ClientDir _deploy\client-release\Mesh-win-x64 `
                        -Installer _deploy\artifacts\Mesh-Setup-vX.Y.Z.exe `
                        -CertProfile mesh-cert
```

Then zip the client + installer and upload as usual. The `sign` dotnet global tool
(github.com/dotnet/sign) performs the actual Trusted Signing calls.
