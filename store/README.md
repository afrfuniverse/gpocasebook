# Microsoft Store submission

GPOCasebook is a Windows 11 x64 desktop application. Build an MSIX from the
source folder on Windows with an installed .NET SDK. The script uses the
Windows packaging API to write the package and check its manifest.

## Build the submission package

Create an MSIX product in Microsoft Partner Center and reserve the app's
display name. Open **Product management → Product identity** and copy the
package Name, Publisher, and PublisherDisplayName exactly as shown.
[Microsoft's product identity guide](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)

```powershell
$gpoIdentity = Read-Host 'Package/Identity/Name'
$gpoPublisher = Read-Host 'Package/Identity/Publisher'
$gpoPublisherDisplay = Read-Host 'Package/Properties/PublisherDisplayName'
$gpoReserve = 'GPOCasebook'
.\store\Build-Store.ps1 -IdentityName $gpoIdentity -Publisher $gpoPublisher -PublisherDisplayName $gpoPublisherDisplay -DisplayName $gpoReserve
```

Set `$gpoReserve` to the exact display name if you reserved a different one.
The script writes `GPOCasebook-Store-<version>-x64.msix` under `build\store`
and prints its SHA-256 hash. A `-ValidationOnly` build uses a separate local
identity and produces a file whose name includes `Validation`.

The Store signs the MSIX it distributes after certification.
[Microsoft's signing overview](https://learn.microsoft.com/en-us/windows/apps/publish/get-started)

## Complete the listing

1. Upload the MSIX built with your Partner Center identity under **Packages**.
2. Use [Listing.md](Listing.md) and upload the screenshots from `docs\images`.
3. Enter the public URL for the repository's `PRIVACY.md` and the Issues page
   for support. The URLs become available after publishing the source.
4. Complete the pricing, markets, age ratings, and submission fields in
   Partner Center.
5. Paste [Certification-notes.txt](Certification-notes.txt) into **Notes for
   certification**. Confirm the application opens in a packaged Windows
   session and check collection with a test domain before publication.

Run `.\Build.ps1 -Test` to verify the regular desktop program and installer.
The local package reader validates the MSIX structure. Installation through
the Store and certification run in Microsoft's distribution flow.
